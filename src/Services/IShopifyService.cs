namespace ShopifyOrderAutomation.Services
{
    public interface IShopifyService
    {
        // Używane przez kontroler przy "shipment_confirmed"
        Task<bool> MarkOrderAsOnHold(string orderName);

        // Używane przez kontroler przy "adopted_at_sorting_center"
        Task<bool> MarkOrderAsFulfilled(string orderName, string trackingNumber);

        // Pomocnicze (używane wewnątrz serwisu/kontrolera – zostawiamy publiczne na przyszłość)
        Task<long?> GetOrderIdByName(string orderName);
        Task<long?> GetFulfillmentOrderId(long orderId);
        Task ReleaseFoHoldIfNeededAsync(long fulfillmentOrderId);

        // Wersja fulfill po FO ID (wykorzystywana wewnątrz serwisu)
        Task<bool> MarkOrderAsFulfilled(long fulfillmentOrderId, string trackingNumber);
    }
}