public interface IShopifyService
{
    Task<bool> MarkOrderAsOnHold(string orderName);
    Task<bool> MarkOrderAsFulfilled(string orderName, string trackingNumber);
}