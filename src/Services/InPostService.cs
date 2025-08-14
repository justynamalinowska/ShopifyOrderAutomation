using System.Net.Http.Headers;
using System.Text.Json;
using ShopifyOrderAutomation.Services;

public class InPostService : IInPostService
{
    private readonly HttpClient _httpClient;
    private readonly string _token;
    private readonly ILogger<InPostService> _logger;

    private const string BaseUrl = "https://api-shipx-pl.easypack24.net/v1";

    public InPostService(HttpClient httpClient, IConfiguration config, ILogger<InPostService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        _token = (config["InPost:Token"] ?? throw new ArgumentNullException("InPost:Token")).Trim();

        _logger.LogInformation("[InPost] Token prefix: {Prefix}", _token.Substring(0, 20));

        // Ustawiamy domyślne nagłówki tylko raz
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<(bool isReady, string shipmentName)> IsReadyForFulfillment(string trackingNumber)
    {
        var url = $"{BaseUrl}/tracking/{trackingNumber}";
        _logger.LogInformation("[InPost] Sprawdzanie statusu przesyłki {TrackingNumber}", trackingNumber);

        var response = await _httpClient.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();

        _logger.LogInformation("[InPost] GET {Url} -> {StatusCode}", url, (int)response.StatusCode);
        _logger.LogDebug("[InPost] Response Body: {Body}", body);

        if (!response.IsSuccessStatusCode)
            return (false, null);

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("status", out var statusProp) ||
            !doc.RootElement.TryGetProperty("tracking_number", out var trackingProp))
        {
            _logger.LogWarning("[InPost] Brak wymaganych pól w odpowiedzi API");
            return (false, null);
        }

        string status = statusProp.GetString();
        string shipmentName = trackingProp.GetString();

        bool isReady = true; // TEST
        // bool isReady = status == "adopted_at_sorting_center"; // produkcja
        return (isReady, shipmentName);
    }

    public async Task<string?> ResolveOrderNameAsync(long shipmentId)
    {
        var url = $"{BaseUrl}/shipments/{shipmentId}";
        _logger.LogInformation("[InPost] Pobieranie szczegółów shipment_id={ShipmentId}", shipmentId);

        var response = await _httpClient.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();

        _logger.LogInformation("[InPost] GET {Url} -> {StatusCode}", url, (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[InPost] Błąd pobierania shipmentu {ShipmentId}: kod={StatusCode}, body={Body}", shipmentId, (int)response.StatusCode, body);
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("reference", out var refProp))
        {
            var orderName = refProp.GetString()?.TrimStart('#');
            _logger.LogInformation("[InPost] Odczytany reference={Reference}", orderName);
            return orderName;
        }

        _logger.LogWarning("[InPost] Shipment {ShipmentId} nie zawiera pola 'reference'", shipmentId);
        return null;
    }
}
