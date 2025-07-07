namespace ShopifyOrderAutomation.Services;

public class InPostService : IInPostService
{
    public Task<(bool isReady, string trackingNumber)> IsReadyForFulfillment(string referenceNumber)
    {
        throw new NotImplementedException();
    }
}