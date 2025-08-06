using Microsoft.AspNetCore.Mvc;
using ShopifyOrderAutomation.Models;
using ShopifyOrderAutomation.Services;

namespace ShopifyOrderAutomation.Controllers;

[ApiController]
[Route("api/inpost-webhook")]
public class InPostWebhookController : ControllerBase
{
    private readonly IInPostService _inPostService;
    private readonly IShopifyService _shopifyService;

    public InPostWebhookController(IInPostService inPostService, IShopifyService shopifyService)
    {
        _inPostService = inPostService;
        _shopifyService = shopifyService;
    }

    [HttpPost]
    public async Task<IActionResult> ReceiveWebhook([FromBody] InPostWebhookPayload payload)
    {
        if (payload == null || string.IsNullOrEmpty(payload.ShipmentName))
            return BadRequest("Invalid payload");

        if (payload.Status == "created")
        {
            await _shopifyService.MarkOrderAsOnHold(payload.ShipmentName);
        }
        else if (payload.Status == "adopted_at_sorting_center")
        {
            var (isReady, trackingNumber) = await _inPostService.IsReadyForFulfillment(payload.ShipmentName);
            if (isReady)
            {
                await _shopifyService.MarkOrderAsFulfilled(payload.ShipmentName, trackingNumber);
            }
        }

        return Ok();
    }
}
