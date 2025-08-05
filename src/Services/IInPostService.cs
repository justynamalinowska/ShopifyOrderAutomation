namespace ShopifyOrderAutomation.Services;

public interface IInPostService
{
    public Task<(bool isReady, string trackingNumber)> IsReadyForFulfillment(string shipmentName);
}