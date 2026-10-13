using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Oxira.Design.Api.Infrastructure;
using Oxira.Design.Application.Common;
using Oxira.Design.Application.Features.Chat;
using Oxira.Design.Application.Features.Orders;
using Oxira.Design.Application.Features.Payments;
using Oxira.Design.Application.Features.PlanAi;
using Oxira.Design.Application.Features.Redesign;
using Oxira.Design.Application.Features.Renders;
using Oxira.Design.Infrastructure.Services;

namespace Oxira.Design.Api.Controllers;

/// <summary>Website AI assistant (JSON, text/plain JSON or a URL-encoded form).</summary>
[ApiController, Route("api/chat")]
public class ChatController(ISender mediator) : ControllerBase
{
    /// <summary>One visitor message → the assistant's reply. Saves a lead (and emails the team) when the visitor confirms a request.</summary>
    [HttpPost, EnableRateLimiting("chat")]
    public Task<ChatReply> Send(ChatInput body, CancellationToken ct) => mediator.Send(new SendChatCommand(body), ct);

    /// <summary>The visible history of a chat session.</summary>
    [HttpGet("{sessionId}"), EnableRateLimiting("reads")]
    public Task<List<ChatTurnDto>> History(string sessionId, CancellationToken ct) => mediator.Send(new GetChatQuery(sessionId), ct);
}

/// <summary>Design orders with plan files; the quick package is paid online (Moyasar).</summary>
[ApiController, Route("api/orders")]
public class OrdersController(ISender mediator) : ControllerBase
{
    /// <summary>
    /// The order form (multipart/form-data): package, name*, phone*, email, city, area, style, notes, summary (JSON), lang, page, returnUrl,
    /// company_website (honeypot) and files (plan0…N, source, preview, model; dxf dwg pdf png jpg webp heic zip glb, 15 MB together).
    /// Returns the order number and, for the quick package, the Moyasar payment link.
    /// </summary>
    [HttpPost, EnableRateLimiting("forms"), RequestSizeLimit(26 * 1024 * 1024), RequestFormLimits(MultipartBodyLengthLimit = 26 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    public async Task<OrderReply> Submit(CancellationToken ct)
    {
        var (fields, files) = await Forms.ReadAsync(Request, 24 * 1024 * 1024, ct);
        return await mediator.Send(new SubmitOrderCommand(new OrderInput(fields, files)), ct);
    }

    /// <summary>Whether an order has been paid (after the visitor comes back from the payment page).</summary>
    [HttpGet("{id:int}/payment"), EnableRateLimiting("reads")]
    public Task<PayReply> Payment(int id, CancellationToken ct) => mediator.Send(new GetPaymentQuery(id), ct);
}

/// <summary>Moyasar.</summary>
[ApiController, Route("api/payments")]
public class PaymentsController(ISender mediator) : ControllerBase
{
    /// <summary>
    /// Moyasar's callback for invoices (orders, render packs, listing tours). The body only names the invoice; it is fetched again
    /// from Moyasar to check that it is paid. Always 200.
    /// </summary>
    [HttpPost("moyasar/callback")]
    public async Task<IActionResult> Callback([FromBody] JsonElement body, CancellationToken ct)
    {
        foreach (var id in InvoiceIds(body))
            if (await mediator.Send(new PaymentCallbackCommand(id), ct)) break;
        return Ok(new { success = true });
    }

    /// <summary>invoice ids the body may carry: an invoice object, or a webhook event about a payment of an invoice</summary>
    public static IEnumerable<string> InvoiceIds(JsonElement body)
    {
        static string? S(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var data = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("data", out var d) ? d : default;
        return new[] { S(data, "invoice_id"), S(body, "invoice_id"), S(body, "id"), S(data, "id") }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct().Take(4);
    }

    /// <summary>Development (Payments:Simulate): opening a simulated invoice link pays it and returns to the site.</summary>
    [HttpGet("simulate/{id}"), ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> Simulate(string id, [FromServices] IServiceProvider sp, CancellationToken ct)
    {
        if (sp.GetService<ISimulatedCheckout>() is not { } checkout) return NotFound();
        var next = checkout.Pay(id);
        if (next == null) return NotFound();
        await mediator.Send(new PaymentCallbackCommand(id), ct);
        return Redirect(next);
    }
}

/// <summary>AI renders of the studio's 3D view (free daily quota, then wallet credits).</summary>
[ApiController, Route("api/renders")]
public class RendersController(ISender mediator) : ControllerBase
{
    /// <summary>
    /// {sessionId, image (data URL), style modern|classic|najdi|scandi|luxury, view top|orbit|walk, room, lang, source snapshot|traced, ref?, wallet?}
    /// → {success, image, remaining, credits, reason invalid|limit|credits|failed}.
    /// </summary>
    [HttpPost, EnableRateLimiting("render"), RequestSizeLimit(8 * 1024 * 1024)]
    public Task<RenderReply> Render(RenderInput body, CancellationToken ct) => mediator.Send(new RenderCommand(body), ct);
}

/// <summary>"Redesign my room": a photo of a real room in a new style.</summary>
[ApiController, Route("api/redesign")]
public class RedesignController(ISender mediator) : ControllerBase
{
    /// <summary>{sessionId, wallet, style, room living|bedroom|majlis|kitchen|bathroom|dining|office|kids, lang, image} → {success, image, free_left, credits, reason}.</summary>
    [HttpPost("renders"), EnableRateLimiting("render"), RequestSizeLimit(6 * 1024 * 1024)]
    public Task<RoomReply> Render(RoomInput body, CancellationToken ct) => mediator.Send(new RedesignCommand(body), ct);
}

/// <summary>Render credits shared by the studio and the room redesign page. Live balance: SignalR /hubs/design → JoinWallet(id) → "wallet".</summary>
[ApiController, Route("api/wallets")]
public class WalletsController(ISender mediator) : ControllerBase
{
    /// <summary>Credits of a wallet and the free room renders left today for this visitor.</summary>
    [HttpGet("{wallet}"), EnableRateLimiting("reads")]
    public Task<WalletReply> Balance(string wallet, [FromQuery] string? sessionId, CancellationToken ct) => mediator.Send(new WalletQuery(wallet, sessionId), ct);

    /// <summary>Buy a pack (p10 29 SAR / 10, p30 69 / 30, p100 179 / 100, VAT included): {wallet, pack, email, lang, returnUrl} → {success, payUrl, purchaseId}.</summary>
    [HttpPost("purchases"), EnableRateLimiting("forms")]
    public Task<PurchaseReply> Buy(PurchaseInput body, CancellationToken ct) => mediator.Send(new BuyPackCommand(body), ct);
}

/// <summary>Claude for the plan designer (/plan/).</summary>
[ApiController, Route("api/plan-ai")]
public class PlanAiController(ISender mediator) : ControllerBase
{
    /// <summary>
    /// kind brief: {text, lang, region, brief} → {success, patch, reply}; kind image: {image, knownWidth, lang} → {success, width, depth, rooms, notes}.
    /// reason: limit | empty | image | ai | parse | unreadable.
    /// </summary>
    [HttpPost, EnableRateLimiting("ai"), RequestSizeLimit(8 * 1024 * 1024)]
    public Task<PlanAiReply> Ask(PlanAiInput body, CancellationToken ct) => mediator.Send(new PlanAiCommand(body), ct);
}
