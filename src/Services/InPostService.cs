using System.Net.Http.Headers;
using System.Text.Json;

namespace ShopifyOrderAutomation.Services;

public class InPostService : IInPostService
{
    private readonly HttpClient _httpClient;
    private readonly string _token;
    private readonly ILogger<InPostService> _logger;

    // produkcyjny ShipX
    private const string BaseUrl = "https://api-shipx-pl.easypack24.net";

    public InPostService(HttpClient httpClient, IConfiguration config, ILogger<InPostService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        _token = config["InPost:Token"] ?? throw new ArgumentNullException("InPost:Token");

        // ustawiamy bazowy adres i nagłówki
        _httpClient.BaseAddress = new Uri(BaseUrl);
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _token);
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<(bool isReady, string shipmentName)> IsReadyForFulfillment(string trackingNumber)
    {
        _logger.LogInformation("[InPost] Sprawdzanie statusu przesyłki {TrackingNumber}", trackingNumber);

        var response = await _httpClient.GetAsync($"/v1/tracking/{trackingNumber}");
        var responseBody = await response.Content.ReadAsStringAsync();

        _logger.LogInformation("[InPost] Status Code: {StatusCode}", (int)response.StatusCode);
        _logger.LogDebug("[InPost] Response Body: {Body}", responseBody);

        if (!response.IsSuccessStatusCode)
            return (false, null);

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        if (!root.TryGetProperty("status", out var statusProp) ||
            !root.TryGetProperty("tracking_number", out var trackingProp))
        {
            _logger.LogWarning("[InPost] Brak wymaganych pól w odpowiedzi API");
            return (false, null);
        }

        string status = statusProp.GetString();
        string shipmentName = trackingProp.GetString();

        bool isReady = true; // TEST: zawsze gotowe
        // bool isReady = status == "adopted_at_sorting_center"; // PRODUKCJA: tylko po przyjęciu w sortowni

        return (isReady, shipmentName);
    }
    
    public async Task<string?> ResolveOrderNameAsync(long shipmentId)
    {
        _logger.LogInformation("[InPost] Pobieranie szczegółów shipment_id={ShipmentId}", shipmentId);

        var response = await _httpClient.GetAsync($"/v1/shipments/{shipmentId}");
        _logger.LogInformation("[InPost] GET /v1/shipments/{ShipmentId} -> {StatusCode}", shipmentId, (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[InPost] Nie udało się pobrać shipmentu {ShipmentId}, kod={StatusCode}", shipmentId, (int)response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("reference", out var refProp))
        {
            var orderName = refProp.GetString()?.TrimStart('#'); // usuwamy #
            return orderName;
        }

        _logger.LogWarning("[InPost] Shipment {ShipmentId} nie zawiera pola 'reference'", shipmentId);
        return null;
    }
}
