namespace ShopifyOrderAutomation.Services;

using System.Net.Http.Headers;
using System.Text.Json;

public class ShopifyService : IShopifyService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly string _shopName;

    public ShopifyService(HttpClient httpClient, IConfiguration config)
    {
        _httpClient = httpClient;
        _config = config;
        _shopName = _config["Shopify:ShopName"];
    }

    public async Task MarkOrderAsOnHold(string orderName)
    {
        var orderId = await GetOrderIdByName(orderName);
        if (orderId == null) return;

        var request = new HttpRequestMessage(HttpMethod.Put,
            $"https://{_shopName}.myshopify.com/admin/api/2023-01/orders/{orderId}.json")
        {
            Content = JsonContent.Create(new
            {
                order = new { id = orderId, tags = "OnHold" }
            })
        };
        AddAuthHeaders(request);
        await _httpClient.SendAsync(request);
    }

    public async Task MarkOrderAsFulfilled(string orderName, string trackingNumber)
    {
        var orderId = await GetOrderIdByName(orderName);
        if (orderId == null) return;

        var fulfillment = new
        {
            fulfillment = new
            {
                tracking_number = trackingNumber,
                tracking_company = "InPost",
                notify_customer = true
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://{_shopName}.myshopify.com/admin/api/2023-01/orders/{orderId}/fulfillments.json")
        {
            Content = JsonContent.Create(fulfillment)
        };
        AddAuthHeaders(request);
        await _httpClient.SendAsync(request);
    }

    private async Task<string> GetOrderIdByName(string orderName)
    {
        var cleanOrderName = orderName.Replace("#", "");
        Console.WriteLine("Szukam zamówienia o nazwie: " + cleanOrderName);

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://{_shopName}.myshopify.com/admin/api/2023-01/orders.json?limit=250&status=any&fulfillment_status=unfulfilled");

        AddAuthHeaders(request);
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);
        var order = json.RootElement.GetProperty("orders").EnumerateArray().FirstOrDefault();
        return order.TryGetProperty("id", out var id) ? id.ToString() : null;
    }

    private void AddAuthHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("X-Shopify-Access-Token", _config["Shopify:Token"]);
    }
}
