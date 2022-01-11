﻿namespace EShopOnAbp.PublicWeb
{
    public static class EShopOnAbpPaymentConsts
    {
        public const string Currency = "USD";
        public const string PaymentIdCookie = "selected_payment_id"; // Setted in payment-widget.js

        public static class DemoAddressTypes
        {
            public const string Work = "Work";
            public const string Home = "Home";
        }
    }
}