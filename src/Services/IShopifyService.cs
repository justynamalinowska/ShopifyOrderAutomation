namespace ShopifyOrderAutomation.Services;

public interface IShopifyService
{
    Task MarkOrderAsFulfilled(long orderId, string trackingNumber);
}
