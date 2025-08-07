using System.Net.Http.Headers;
using System.Text.Json;

namespace ShopifyOrderAutomation.Services;

public class InPostService : IInPostService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;

    public InPostService(HttpClient httpClient, IConfiguration config)
    {
        _httpClient = httpClient;
        _config = config;
    }

    public async Task<(bool isReady, string shipmentName)> IsReadyForFulfillment(string trackingNumber)
    {
        string token = _config["InPost:Token"];
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api-shipx-pl.easypack24.net/v1/tracking/{trackingNumber}"
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Console.WriteLine($"[InPost] Wysyłam zapytanie do InPost: {request.RequestUri}");

        var response = await _httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"[InPost] Status Code: {(int)response.StatusCode}");
        Console.WriteLine($"[InPost] Response Body: {responseBody}");

        if (!response.IsSuccessStatusCode) return (false, null);

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        string status = root.GetProperty("status").GetString();
        string shipmentName = root.GetProperty("tracking_number").GetString();

        bool isReady = true; //status == "adopted_at_sorting_center";
        return (isReady, shipmentName);
    }
}
