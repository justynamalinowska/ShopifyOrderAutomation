namespace ShopifyOrderAutomation.Services;

public interface IInPostService
{
    Task<(bool isReady, string shipmentName)> IsReadyForFulfillment(string trackingNumber);
    Task<string?> ResolveOrderNameAsync(long shipmentId);
}
