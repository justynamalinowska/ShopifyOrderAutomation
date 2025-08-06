namespace ShopifyOrderAutomation.Services;

public interface IInPostService
{
    Task<(bool isReady, string trackingNumber)> IsReadyForFulfillment(string shipmentName);
}
