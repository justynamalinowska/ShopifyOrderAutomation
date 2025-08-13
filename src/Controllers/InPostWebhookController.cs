using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using ShopifyOrderAutomation.Models;
using ShopifyOrderAutomation.Services;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;

namespace ShopifyOrderAutomation.Controllers;

[ApiController]
[Route("api/inpost-webhook")]
public class InPostWebhookController : ControllerBase
{
    private readonly IInPostService _inPostService;
    private readonly IShopifyService _shopifyService;
    private readonly ILogger<InPostWebhookController> _logger;

    public InPostWebhookController(IInPostService inPostService, IShopifyService shopifyService, ILogger<InPostWebhookController> logger)
    {
        _inPostService = inPostService;
        _shopifyService = shopifyService;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult HealthCheck()
    {
        return Ok("ok");
    }
    
    [HttpPost]
    public async Task<IActionResult> ReceiveWebhook()
    {
        // 1) wczytanie surowego body (zadziała zawsze)
        using var reader = new StreamReader(Request.Body);
        var raw = await reader.ReadToEndAsync();
        _logger.LogInformation("InPost webhook RAW: {Raw}", raw);

        try
        {
            var json = JsonNode.Parse(raw)!.AsObject();

            // 2) PRZYPADek A: prawdziwy webhook InPost (owijka event + payload)
            if (json.TryGetPropertyValue("event", out var evNode) &&
                json.TryGetPropertyValue("payload", out var payloadNode))
            {
                var ev = evNode!.GetValue<string>();
                var payload = payloadNode!.AsObject();

                var status   = payload.TryGetPropertyValue("status", out var s) ? s!.GetValue<string>() : null;
                var tracking = payload.TryGetPropertyValue("tracking_number", out var t) ? t!.GetValue<string>() : null;

                _logger.LogInformation("InPost EVENT={Event} STATUS={Status} TRACKING={Tracking}",
                    ev, status, tracking);

                // Mapowanie minimalne – to, co już masz w logice:
                if (ev == "shipment_confirmed")
                {
                    var shipmentId = payload.TryGetPropertyValue("shipment_id", out var sh) ? sh!.GetValue<long>() : 0;
                    var orderName = await _inPostService.ResolveOrderNameAsync(shipmentId);

                    _logger.LogInformation("shipment_confirmed -> orderName={Order}", orderName ?? "null");

                    if (!string.IsNullOrWhiteSpace(orderName))
                    {
                        await _shopifyService.MarkOrderAsOnHold(orderName);
                    }
                    else
                    {
                        _logger.LogWarning("Nie udało się ustalić numeru zamówienia dla shipment_id={ShipmentId}", shipmentId);
                    }
                }
                else if (ev == "shipment_status_changed" && status == "adopted_at_sorting_center")
                {
                    var (isReady, tn) = await _inPostService.IsReadyForFulfillment(tracking);
                    if (isReady)
                    {
                        // jw. potrzebna nazwa zamówienia – do zrobienia gdy będziesz mieć mapowanie
                        _logger.LogInformation("Gotowe do realizacji (tracking={Tracking}).", tn);
                        // await _shopifyService.MarkOrderAsFulfilled(orderName, tn);
                    }
                }

                return Ok();
            }

            // 3) PRZYPADek B: Twój testowy „płaski” JSON z curl (zostawiam, bo jest wygodny do testów)
            var flat = JsonSerializer.Deserialize<FlatTestPayload>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (flat is null) return BadRequest("Invalid payload (unknown shape)");

            _logger.LogInformation("Flat payload: {Status} {ShipmentName} {Tracking}",
                flat.Status, flat.ShipmentName, flat.TrackingNumber);

            switch (flat.Status)
            {
                case "created":
                    if (!string.IsNullOrWhiteSpace(flat.ShipmentName))
                        await _shopifyService.MarkOrderAsOnHold(flat.ShipmentName!);
                    break;

                case "adopted_at_sorting_center":
                    var (isReady, trackingNumber) = await _inPostService.IsReadyForFulfillment(flat.TrackingNumber);
                    if (isReady && !string.IsNullOrWhiteSpace(flat.ShipmentName))
                        await _shopifyService.MarkOrderAsFulfilled(flat.ShipmentName!, trackingNumber);
                    break;
            }

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process InPost webhook");
            // 200, żeby InPost nie spamował ponownie — logi są.
            return Ok();
        }
    }
}
record FlatTestPayload(
    string? Status,
    [property: JsonPropertyName("shipment_name")] string? ShipmentName,
    [property: JsonPropertyName("tracking_number")] string? TrackingNumber
);


