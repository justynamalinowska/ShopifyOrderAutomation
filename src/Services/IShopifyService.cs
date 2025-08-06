namespace ShopifyOrderAutomation.Services;

public interface IShopifyService
{
    Task MarkOrderAsOnHold(string orderName);
    Task MarkOrderAsFulfilled(string orderName, string trackingNumber);
}