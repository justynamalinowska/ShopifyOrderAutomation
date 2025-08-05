namespace ShopifyOrderAutomation.Services;

using Microsoft.AspNetCore.Mvc;
using ShopifyOrderAutomation.Services;
using ShopifyOrderAutomation.Models;

namespace ShopifyOrderAutomation.Controllers;

[ApiController]
[Route("api/webhook")]
public class WebhookController : ControllerBase
{
    private readonly IInPostService _inPostService;
    private readonly IShopifyService _shopifyService;

    public WebhookController(IInPostService inPostService, IShopifyService shopifyService)
    {
        _inPostService = inPostService;
        _shopifyService = shopifyService;
    }

    [HttpPost("fulfillment-created")]
    public async Task<IActionResult> FulfillmentCreated([FromBody] ShopifyFulfillmentWebhook payload)
    {
        var shipmentName = payload.OrderName; 

        var (isReady, trackingNumber) = await _inPostService.IsReadyForFulfillment(shipmentName);

        if (isReady)
        {
            await _shopifyService.MarkOrderAsFulfilled(payload.OrderId, trackingNumber);
        }

        return Ok();
    }
}
