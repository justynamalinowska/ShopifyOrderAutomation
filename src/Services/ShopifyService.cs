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
                tracking_number = trackingNumber
            }
        };

        string json = JsonSerializer.Serialize(body);
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode(); 
    }
}
