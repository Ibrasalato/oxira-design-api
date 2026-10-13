using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Oxira.Design.Api.Infrastructure;
using Oxira.Design.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Oxira.Design.Application.Features.Account;
using Oxira.Design.Application.Emails;
using Oxira.Design.Application.Features.Chat;
using Oxira.Design.Application.Features.Costs;
using Oxira.Design.Application.Features.Finishing;
using Oxira.Design.Application.Features.Growth;
using Oxira.Design.Application.Features.Listings;
using Oxira.Design.Application.Features.Orders;
using Oxira.Design.Application.Features.Payments;
using Oxira.Design.Application.Features.PlanAi;
using Oxira.Design.Application.Features.Public;
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

/// <summary>The signed-in Oxira account (Bearer JWT from oxira-web-api): everything the /account/ page shows.</summary>
[ApiController, Route("api/account"), Authorize]
public class AccountController(ISender mediator) : ControllerBase
{
    /// <summary>Email, designer status (""|pending|active|admin), saved plans, own orders with threads; open and claimed orders for designers.</summary>
    [HttpGet, EnableRateLimiting("reads")]
    public Task<AccountView> Load(CancellationToken ct) => mediator.Send(new AccountQuery(), ct);
}

/// <summary>Saved plans of the signed-in account (≤ 50, ≤ 400 KB each) with share links and the public gallery switch.</summary>
[ApiController, Route("api/projects"), Authorize]
public class ProjectsController(ISender mediator) : ControllerBase
{
    [HttpGet("{pid}"), EnableRateLimiting("reads")]
    public Task<ProjectReply> Get(string pid, CancellationToken ct) => mediator.Send(new GetProjectQuery(pid), ct);

    /// <summary>Creates a plan, or updates it when pid is one of yours. Returns pid and share id.</summary>
    [HttpPost, EnableRateLimiting("reads"), RequestSizeLimit(2 * 1024 * 1024)]
    public Task<SavedReply> Save(SaveProjectInput body, CancellationToken ct) => mediator.Send(new SaveProjectCommand(body), ct);

    /// <summary>Show or hide the plan in the public gallery (the team is emailed).</summary>
    [HttpPost("{pid}/publish"), EnableRateLimiting("forms")]
    public Task<PublishReply> Publish(string pid, PublishInput body, CancellationToken ct) => mediator.Send(new PublishProjectCommand(pid, body.Public), ct);

    [HttpDelete("{pid}")]
    public Task<Reply> Delete(string pid, CancellationToken ct) => mediator.Send(new DeleteProjectCommand(pid), ct);
}

/// <summary>An order's thread, the designer portal (claim, deliver) and the client's rating. Live: SignalR "order" events.</summary>
[ApiController, Route("api/orders/{id:int}"), Authorize]
public class OrderWorkController(ISender mediator) : ControllerBase
{
    /// <summary>A message to the other side (client ↔ designer or team); they get an email.</summary>
    [HttpPost("comments"), EnableRateLimiting("forms")]
    public Task<Reply> Comment(int id, CommentInput body, CancellationToken ct) => mediator.Send(new CommentCommand(id, body.Text), ct);

    /// <summary>Designers: take an open order (first one wins; status becomes progress).</summary>
    [HttpPost("claim"), EnableRateLimiting("forms")]
    public Task<Reply> Claim(int id, CancellationToken ct) => mediator.Send(new ClaimOrderCommand(id), ct);

    /// <summary>The client rates a delivered order (1–5, review ≤ 1000 characters).</summary>
    [HttpPost("rating"), EnableRateLimiting("forms")]
    public Task<Reply> Rate(int id, RateInput body, CancellationToken ct) => mediator.Send(new RateOrderCommand(id, body.Rating, body.Review), ct);

    /// <summary>The order's designer sends the files (multipart: note + files, ≤ 20 MB); the client gets them by email, status delivered.</summary>
    [HttpPost("delivery"), EnableRateLimiting("forms"), RequestSizeLimit(26 * 1024 * 1024), RequestFormLimits(MultipartBodyLengthLimit = 26 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    public async Task<Reply> Deliver(int id, CancellationToken ct)
    {
        var (fields, files) = await Forms.ReadAsync(Request, 24 * 1024 * 1024, ct);
        return await mediator.Send(new DeliverCommand(id, fields.GetValueOrDefault("note"), files), ct);
    }
}

/// <summary>The public designer directory, and applications to join.</summary>
[ApiController, Route("api/designers")]
public class DesignersController(ISender mediator) : ControllerBase
{
    /// <summary>Active designers with their rating and recent reviews (no emails).</summary>
    [HttpGet, EnableRateLimiting("reads")]
    public Task<ListReply<DesignerCard>> List(CancellationToken ct) => mediator.Send(new DesignersQuery(), ct);

    /// <summary>The signed-in account applies as a designer (status pending until the team sets it active).</summary>
    [HttpPost("apply"), Authorize, EnableRateLimiting("forms")]
    public Task<Reply> Apply(ApplyInput body, CancellationToken ct) => mediator.Send(new ApplyDesignerCommand(body), ct);
}

/// <summary>Public plan pages.</summary>
[ApiController, Route("api")]
public class PublicController(ISender mediator) : ControllerBase
{
    /// <summary>A plan shared by link (/plan/?s=…), read-only.</summary>
    [HttpGet("share/{s}"), EnableRateLimiting("reads")]
    public Task<SharedPlanReply> Shared(string s, CancellationToken ct) => mediator.Send(new SharedPlanQuery(s), ct);

    /// <summary>The 36 newest published plans.</summary>
    [HttpGet("gallery"), EnableRateLimiting("reads")]
    public Task<ListReply<GalleryItem>> Gallery(CancellationToken ct) => mediator.Send(new GalleryQuery(), ct);

    /// <summary>Oxira Design Pro waitlist: {email, plan, role, lang, country, company_website}.</summary>
    [HttpPost("waitlist"), EnableRateLimiting("forms")]
    public Task<Reply> Waitlist(WaitlistInput body, CancellationToken ct) => mediator.Send(new JoinWaitlistCommand(body), ct);
}

/// <summary>3D listing tours (/tour/?id=): a draft until the 49 SAR invoice is paid.</summary>
[ApiController, Route("api/listings")]
public class ListingsController(ISender mediator) : ControllerBase
{
    /// <summary>{title, city, district, price, phone, email, lang, style, plan} → Claude ad copy, draft, Moyasar link: {success, id, payUrl}. reason invalid | plan | limit.</summary>
    [HttpPost, EnableRateLimiting("forms"), RequestSizeLimit(2 * 1024 * 1024)]
    public Task<ListingReply> Create(ListingInput body, CancellationToken ct) => mediator.Send(new CreateListingCommand(body), ct);

    /// <summary>The tour page's data (the owner's phone only once the listing is active).</summary>
    [HttpGet("{id}"), EnableRateLimiting("reads")]
    public Task<ListingView> Get(string id, CancellationToken ct) => mediator.Send(new GetListingQuery(id), ct);
}

/// <summary>Finishing-quote requests from the studio (quantities + contact; 3 per IP per day).</summary>
[ApiController, Route("api/finishing")]
public class FinishingController(ISender mediator) : ControllerBase
{
    [HttpPost, EnableRateLimiting("forms")]
    public Task<IdReply> Submit(FinishingInput body, CancellationToken ct) => mediator.Send(new FinishingCommand(body), ct);
}

/// <summary>The build-cost calculator (/cost/).</summary>
[ApiController, Route("api/costs")]
public class CostsController(ISender mediator, Microsoft.Extensions.Options.IOptions<SiteOptions> site) : ControllerBase
{
    /// <summary>The newest approved rates (the page merges them over its bundled ones). Cached for an hour.</summary>
    [HttpGet("rates"), EnableRateLimiting("reads")]
    public async Task<CostRatesReply> Rates(CancellationToken ct)
    {
        Response.Headers.CacheControl = "public, max-age=3600";
        return await mediator.Send(new CostRatesQuery(), ct);
    }

    /// <summary>Contractor-quote request (5 per IP, 300 per day; company_website is a honeypot).</summary>
    [HttpPost("quotes"), EnableRateLimiting("forms")]
    public Task<IdReply> Quote(BuildQuoteInput body, CancellationToken ct) => mediator.Send(new BuildQuoteCommand(body), ct);

    /// <summary>The approval link in the monthly rates email. Returns a small HTML page.</summary>
    [HttpGet("approve")]
    public async Task<ContentResult> Approve([FromQuery] string? t, CancellationToken ct)
    {
        var ok = await mediator.Send(new ApproveRatesCommand(t ?? ""), ct);
        return new ContentResult { Content = Templates.ApprovedPage(ok, site.Value), ContentType = "text/html; charset=utf-8", StatusCode = ok ? 200 : 404 };
    }
}

/// <summary>The unsubscribe link in follow-up emails. Returns a small HTML page.</summary>
[ApiController, Route("api/optout")]
public class OptOutController(ISender mediator, Microsoft.Extensions.Options.IOptions<SiteOptions> site) : ControllerBase
{
    [HttpGet]
    public async Task<ContentResult> OptOut([FromQuery] string? e, [FromQuery] string? t, CancellationToken ct)
    {
        var ok = await mediator.Send(new OptOutCommand(e, t), ct);
        return new ContentResult { Content = Templates.OptOutPage(ok, site.Value), ContentType = "text/html; charset=utf-8", StatusCode = ok ? 200 : 400 };
    }
}
