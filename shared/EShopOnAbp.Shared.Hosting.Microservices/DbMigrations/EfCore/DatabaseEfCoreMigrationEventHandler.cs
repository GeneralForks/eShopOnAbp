﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.DistributedLocking;
using Volo.Abp.Domain.Entities.Events.Distributed;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Uow;

namespace EShopOnAbp.Shared.Hosting.Microservices.DbMigrations.EfCore;

public abstract class DatabaseEfCoreMigrationEventHandler<TDbContext> : DatabaseMigrationEventHandlerBase
    where TDbContext : DbContext, IEfCoreDbContext
{
    protected const string TryCountPropertyName = "TryCount";
    protected const int MaxEventTryCount = 3;

    protected ICurrentTenant CurrentTenant { get; }
    protected IUnitOfWorkManager UnitOfWorkManager { get; }
    protected ITenantStore TenantStore { get; }
    protected IDistributedEventBus DistributedEventBus { get; }
    protected ILogger<DatabaseEfCoreMigrationEventHandler<TDbContext>> Logger { get; set; }
    protected string DatabaseName { get; }
    protected IAbpDistributedLock DistributedLockProvider { get; }

    protected DatabaseEfCoreMigrationEventHandler(
        ICurrentTenant currentTenant,
        IUnitOfWorkManager unitOfWorkManager,
        ITenantStore tenantStore,
        IDistributedEventBus distributedEventBus,
        string databaseName,
        IAbpDistributedLock distributedLockProvider
        )
    {
        CurrentTenant = currentTenant;
        UnitOfWorkManager = unitOfWorkManager;
        TenantStore = tenantStore;
        DatabaseName = databaseName;
        DistributedEventBus = distributedEventBus;
        DistributedLockProvider = distributedLockProvider;

        Logger = NullLogger<DatabaseEfCoreMigrationEventHandler<TDbContext>>.Instance;
    }

    /// <summary>
    /// Apply pending EF Core schema migrations to the database.
    /// Returns true if any migration has applied.
    /// </summary>
    protected virtual async Task<bool> MigrateDatabaseSchemaAsync(Guid? tenantId)
    {
        var result = false;

        using (CurrentTenant.Change(tenantId))
        {
            using (var uow = UnitOfWorkManager.Begin(requiresNew: true, isTransactional: false))
            {
                async Task<bool> MigrateDatabaseSchemaWithDbContextAsync()
                {
                    var dbContext = await uow.ServiceProvider
                        .GetRequiredService<IDbContextProvider<TDbContext>>()
                        .GetDbContextAsync();

                    if ((await dbContext.Database.GetPendingMigrationsAsync()).Any())
                    {
                        await dbContext.Database.MigrateAsync();
                        return true;
                    }

                    return false;
                }

                if (tenantId == null)
                {
                    //Migrating the host database
                    Log.Information($"There is no tenant. Migrating {DatabaseName}...");
                    result = await MigrateDatabaseSchemaWithDbContextAsync();
                }
                else
                {
                    var tenantConfiguration = await TenantStore.FindAsync(tenantId.Value);
                    if (!tenantConfiguration.ConnectionStrings.Default.IsNullOrWhiteSpace() ||
                        !tenantConfiguration.ConnectionStrings.GetOrDefault(DatabaseName).IsNullOrWhiteSpace())
                    {
                        //Migrating the tenant database (only if tenant has a separate database)
                        Log.Information($"Migrating tenant database:{DatabaseName} with tenantId:{tenantId}...");
                        result = await MigrateDatabaseSchemaWithDbContextAsync();
                    }
                }

                await uow.CompleteAsync();
            }
        }

        return result;
    }

    protected virtual async Task HandleErrorOnApplyDatabaseMigrationAsync(
        ApplyDatabaseMigrationsEto eventData,
        Exception exception)
    {
        var tryCount = IncrementEventTryCount(eventData);
        if (tryCount <= MaxEventTryCount)
        {
            Log.Warning(
                $"Could not apply database migrations. Re-queueing the operation. TenantId = {eventData.TenantId}, Database Name = {eventData.DatabaseName}.");
            Log.Error(exception.ToString());

            await Task.Delay(RandomHelper.GetRandom(5000, 15000));
            Log.Warning("Re publishing the event!");
            await DistributedEventBus.PublishAsync(eventData);
        }
        else
        {
            Log.Error(
                $"Could not apply database migrations. Canceling the operation. TenantId = {eventData.TenantId}, DatabaseName = {eventData.DatabaseName}.");
            Log.Error(exception.ToString());
        }
    }

    protected virtual async Task HandleErrorTenantCreatedAsync(
        TenantCreatedEto eventData,
        Exception exception)
    {
        var tryCount = IncrementEventTryCount(eventData);
        if (tryCount <= MaxEventTryCount)
        {
            Log.Warning(
                $"Could not perform tenant created event. Re-queueing the operation. TenantId = {eventData.Id}, TenantName = {eventData.Name}.");
            Logger.LogException(exception, LogLevel.Warning);

            await Task.Delay(RandomHelper.GetRandom(5000, 15000));
            await DistributedEventBus.PublishAsync(eventData);
        }
        else
        {
            Logger.LogError(
                $"Could not perform tenant created event. Canceling the operation. TenantId = {eventData.Id}, TenantName = {eventData.Name}.");
            Logger.LogException(exception);
        }
    }

    protected virtual async Task HandleErrorTenantConnectionStringUpdatedAsync(
        TenantConnectionStringUpdatedEto eventData,
        Exception exception)
    {
        var tryCount = IncrementEventTryCount(eventData);
        if (tryCount <= MaxEventTryCount)
        {
            Logger.LogWarning(
                $"Could not perform tenant connection string updated event. Re-queueing the operation. TenantId = {eventData.Id}, TenantName = {eventData.Name}.");
            Logger.LogException(exception, LogLevel.Warning);

            await Task.Delay(RandomHelper.GetRandom(5000, 15000));
            await DistributedEventBus.PublishAsync(eventData);
        }
        else
        {
            Logger.LogError(
                $"Could not perform tenant connection string updated event. Canceling the operation. TenantId = {eventData.Id}, TenantName = {eventData.Name}.");
            Logger.LogException(exception);
        }
    }

    private static int GetEventTryCount(EtoBase eventData)
    {
        var tryCountAsString = eventData.Properties.GetOrDefault(TryCountPropertyName);
        if (tryCountAsString.IsNullOrEmpty())
        {
            return 0;
        }

        return int.Parse(tryCountAsString);
    }

    private static void SetEventTryCount(EtoBase eventData, int count)
    {
        eventData.Properties[TryCountPropertyName] = count.ToString();
    }

    private static int IncrementEventTryCount(EtoBase eventData)
    {
        var count = GetEventTryCount(eventData) + 1;
        SetEventTryCount(eventData, count);
        return count;
    }
}