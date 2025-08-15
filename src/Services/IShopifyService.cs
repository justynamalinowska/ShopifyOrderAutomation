namespace ShopifyOrderAutomation.Services
{
    public interface IShopifyService
    {
        Task<bool> MarkOrderAsOnHold(string orderName);
        Task<bool> MarkOrderAsFulfilled(string orderName, string trackingNumber);
        Task<long?> GetOrderIdByName(string orderName);
        Task<long?> GetFulfillmentOrderId(long orderId);
        Task ReleaseFoHoldIfNeededAsync(long fulfillmentOrderId);
        Task<bool> MarkOrderAsFulfilled(long fulfillmentOrderId, string trackingNumber);
    }
}