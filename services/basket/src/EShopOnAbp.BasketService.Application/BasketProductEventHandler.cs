﻿using System;
using System.Threading.Tasks;
using EShopOnAbp.CatalogService.Products;
using Volo.Abp.Caching;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Entities.Events.Distributed;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.ObjectMapping;

namespace EShopOnAbp.BasketService;

public class BasketProductEventHandler : IDistributedEventHandler<EntityUpdatedEto<ProductEto>>, ITransientDependency
{
    private readonly IDistributedCache<ProductDto, Guid> _cache;
    private readonly IObjectMapper _objectMapper;

    public BasketProductEventHandler(
        IDistributedCache<ProductDto, Guid> cache,
        IObjectMapper objectMapper)
    {
        _cache = cache;
        _objectMapper = objectMapper;
    }

    public async Task HandleEventAsync(EntityUpdatedEto<ProductEto> eventData)
    {
        var cachedProductDto = await _cache.GetAsync(eventData.Entity.Id);
        if (cachedProductDto == null)
        {
            return;
        }

        _objectMapper.Map(eventData.Entity, cachedProductDto);
        await _cache.SetAsync(eventData.Entity.Id, cachedProductDto);
    }
}