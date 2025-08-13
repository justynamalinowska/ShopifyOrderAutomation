using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using ShopifyOrderAutomation.Services;
using Microsoft.Extensions.Logging;

namespace ShopifyOrderAutomation.Controllers;

[ApiController]
[Route("api/inpost-webhook")]
public class InPostWebhookController : ControllerBase
{
    private readonly IInPostService _inPostService;
    private readonly IShopifyService _shopifyService;
    private readonly ILogger<InPostWebhookController> _logger;

    public InPostWebhookController(
        IInPostService inPostService,
        IShopifyService shopifyService,
        ILogger<InPostWebhookController> logger)
    {
        _inPostService = inPostService;
        _shopifyService = shopifyService;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult HealthCheck() => Ok("ok");

    [HttpPost]
    public async Task<IActionResult> ReceiveWebhook()
    {
        // wczytanie raw body
        using var reader = new StreamReader(Request.Body);
        var raw = await reader.ReadToEndAsync();
        _logger.LogInformation("InPost webhook RAW: {Raw}", raw);

        try
        {
            var json = JsonNode.Parse(raw)?.AsObject();
            if (json is null)
            {
                _logger.LogWarning("Nie udało się sparsować JSON webhooka.");
                return BadRequest();
            }

            if (!json.TryGetPropertyValue("event", out var evNode) ||
                !json.TryGetPropertyValue("payload", out var payloadNode))
            {
                _logger.LogWarning("Webhook bez wymaganych pól 'event' lub 'payload'.");
                return BadRequest();
            }

            var ev = evNode!.GetValue<string>();
            var payload = payloadNode!.AsObject();

            var status = payload.TryGetPropertyValue("status", out var s) ? s!.GetValue<string>() : null;
            var tracking = payload.TryGetPropertyValue("tracking_number", out var t) ? t!.GetValue<string>() : null;
            var shipmentId = payload.TryGetPropertyValue("shipment_id", out var sh) ? sh!.GetValue<long>() : 0;

            _logger.LogInformation("InPost EVENT={Event} STATUS={Status} TRACKING={Tracking} SHIPMENT_ID={ShipmentId}",
                ev, status ?? "(null)", tracking ?? "(null)", shipmentId);

            switch (ev)
            {
                case "shipment_confirmed": // etykieta utworzona → ON HOLD
                    await HandleOnHoldAsync(shipmentId);
                    break;

                case "shipment_status_changed" when status == "adopted_at_sorting_center":
                    await HandleFulfillmentAsync(shipmentId, tracking);
                    break;

                default:
                    _logger.LogInformation("Event {Event} nie wymaga akcji.", ev);
                    break;
            }

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd podczas przetwarzania webhooka InPost.");
            // zwracamy 200 aby InPost nie wysyłał ponownie
            return Ok();
        }
    }

    private async Task HandleOnHoldAsync(long shipmentId)
    {
        var orderName = await _inPostService.ResolveOrderNameAsync(shipmentId);
        if (!string.IsNullOrWhiteSpace(orderName))
        {
            _logger.LogInformation("Oznaczam zamówienie {OrderName} jako ON HOLD.", orderName);
            await _shopifyService.MarkOrderAsOnHold(orderName);
        }
        else
        {
            _logger.LogWarning("Nie udało się ustalić numeru zamówienia dla shipment_id={ShipmentId}", shipmentId);
        }
    }

    private async Task HandleFulfillmentAsync(long shipmentId, string? tracking)
    {
        var orderName = await _inPostService.ResolveOrderNameAsync(shipmentId);
        if (!string.IsNullOrWhiteSpace(orderName))
        {
            var (isReady, tn) = await _inPostService.IsReadyForFulfillment(tracking);
            if (isReady)
            {
                _logger.LogInformation("Oznaczam zamówienie {OrderName} jako FULFILLED (tracking={Tracking}).", orderName, tn);
                await _shopifyService.MarkOrderAsFulfilled(orderName, tn);
            }
            else
            {
                _logger.LogInformation("Zamówienie {OrderName} nie jest jeszcze gotowe do realizacji.", orderName);
            }
        }
        else
        {
            _logger.LogWarning("Nie udało się ustalić numeru zamówienia dla shipment_id={ShipmentId}", shipmentId);
        }
    }
}
