using System.Net.Http.Headers;
using System.Text.Json;

namespace ShopifyOrderAutomation.Services;

public class InPostService : IInPostService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<InPostService> _logger;

    public InPostService(HttpClient httpClient, IConfiguration config, ILogger<InPostService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
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
    
    public async Task<string?> ResolveOrderNameAsync(long shipmentId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api-shipx-pl.easypack24.net/v1/shipments/{shipmentId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config["InPost:Token"]);

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("InPost API GET shipments/{id} zwróciło {Status}", response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("reference", out var refEl) && refEl.ValueKind == JsonValueKind.String)
            return refEl.GetString();

        return null;
    }

}
