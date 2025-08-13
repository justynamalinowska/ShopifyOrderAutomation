using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ShopifyOrderAutomation.Services;

public class InPostService : IInPostService
{
    private readonly HttpClient _httpClient;
    private readonly string _token;
    private readonly ILogger<InPostService> _logger;
    private readonly IConfiguration _config;

    // produkcyjny ShipX
    private const string BaseUrl = "https://api-shipx-pl.easypack24.net";

    public InPostService(HttpClient httpClient, IConfiguration config, ILogger<InPostService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _config = config;

        _token = _config["InPost:Token"] ?? throw new ArgumentNullException("InPost:Token");

        // ustawiamy nagłówki od razu
        _httpClient.BaseAddress = new Uri(BaseUrl);
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _token);
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
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
        var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/shipments/{shipmentId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        var response = await _httpClient.SendAsync(request);
        _logger.LogInformation("InPost GET /v1/shipments/{ShipmentId} -> {StatusCode}", shipmentId, (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Nie udało się pobrać shipmentu {ShipmentId}, kod={StatusCode}", shipmentId, (int)response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("reference", out var refProp))
        {
            var orderName = refProp.GetString();
            return orderName;
        }

        return null;
    }
}
