namespace ShopifyOrderAutomation.Services;

using System.Net.Http.Headers;
using System.Text.Json;

public class ShopifyService : IShopifyService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;

    public ShopifyService(HttpClient httpClient, IConfiguration config)
    {
        _httpClient = httpClient;
        _config = config;
    }

    public async Task MarkOrderAsFulfilled(long orderId, string trackingNumber)
    {
        string shopUrl = _config["Shopify:BaseUrl"];
        string accessToken = _config["Shopify:Token"];

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://{shopUrl}/admin/api/2023-04/orders/{orderId}/fulfillments.json"
        );

        request.Headers.Add("X-Shopify-Access-Token", accessToken);

        var body = new
        {
            fulfillment = new
            {
                notify_customer = true,
                tracking_number = trackingNumber,
                tracking_company = "InPost",
            }
        };

        string json = JsonSerializer.Serialize(body);
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode(); 
    }
    
    public async Task HoldFulfillmentAsync(long orderId)
{
    string shopUrl = _config["Shopify:BaseUrl"];
    string accessToken = _config["Shopify:Token"];

    var request = new HttpRequestMessage(
        HttpMethod.Post,
        $"https://{shopUrl}/admin/api/2023-04/orders/{orderId}/fulfillment_orders.json"
    );
    request.Headers.Add("X-Shopify-Access-Token", accessToken);
    var response = await _httpClient.SendAsync(request);
    var content = await response.Content.ReadAsStringAsync();
    var root = JsonDocument.Parse(content).RootElement;

    foreach (var fulfillmentOrder in root.GetProperty("fulfillment_orders").EnumerateArray())
    {
        var fulfillmentOrderId = fulfillmentOrder.GetProperty("id").GetInt64();
        var holdRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://{shopUrl}/admin/api/2023-04/fulfillment_orders/{fulfillmentOrderId}/hold.json"
        );

        var body = new
        {
            reason = "awaiting_stock",
            reason_notes = "Wstrzymane do momentu potwierdzenia odbioru przez InPost"
        };

        holdRequest.Headers.Add("X-Shopify-Access-Token", accessToken);
        holdRequest.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

        await _httpClient.SendAsync(holdRequest);
    }
}

public async Task ReleaseFulfillmentHoldAsync(long orderId)
{
    string shopUrl = _config["Shopify:BaseUrl"];
    string accessToken = _config["Shopify:Token"];

    var request = new HttpRequestMessage(
        HttpMethod.Post,
        $"https://{shopUrl}/admin/api/2023-04/orders/{orderId}/fulfillment_orders.json"
    );
    request.Headers.Add("X-Shopify-Access-Token", accessToken);
    var response = await _httpClient.SendAsync(request);
    var content = await response.Content.ReadAsStringAsync();
    var root = JsonDocument.Parse(content).RootElement;

    foreach (var fulfillmentOrder in root.GetProperty("fulfillment_orders").EnumerateArray())
    {
        var fulfillmentOrderId = fulfillmentOrder.GetProperty("id").GetInt64();
        var releaseRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://{shopUrl}/admin/api/2023-04/fulfillment_orders/{fulfillmentOrderId}/release_hold.json"
        );

        releaseRequest.Headers.Add("X-Shopify-Access-Token", accessToken);
        await _httpClient.SendAsync(releaseRequest);
    }
}

}