using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oxira.Design.Application.Common;
using Oxira.Design.Application.Emails;
using Oxira.Design.Domain;
using static Oxira.Design.Domain.Text;

namespace Oxira.Design.Application.Features.Listings;

/// <summary>A 3D listing tour from the studio: plan is the studio's plan JSON (rooms + walls, ≤ 600 KB).</summary>
public record ListingInput(string? Title, string? City, string? District, string? Price, string? Phone, string? Email, string? Lang, string? Style, string? Plan);
public record ListingReply(bool Success, string? Id, string? PayUrl, string? Reason = null) : IReply;
public record ListingView(bool Success, string? Id = null, string? Status = null, string? Title = null, string? City = null, string? District = null,
    string? Price = null, double? Area = null, string? Style = null, string? Lang = null, string? Phone = null, JsonElement? Copy = null, string? Plan = null,
    string? Reason = null) : IReply;

public record CreateListingCommand(ListingInput Input) : ICommand<ListingReply>;
public record GetListingQuery(string Id) : IQuery<ListingView>;
/// <summary>called by the payment callback once Moyasar confirmed the invoice</summary>
public record ActivateListingCommand(Invoice Invoice) : ICommand<bool>;

/// <summary>Listing tours: Claude writes the ad copy (Arabic + English), the listing is a draft until its 49 SAR invoice is paid.</summary>
public class ListingHandlers(IAppDbContext db, TimeProvider clock, IQuota quota, IAnthropic ai, IPayments payments, IEmailSender email, ICurrentUser me,
    IOptions<FormLimitOptions> opt, IOptions<SiteOptions> site, ILogger<ListingHandlers> log)
    : IRequestHandler<CreateListingCommand, ListingReply>, IRequestHandler<GetListingQuery, ListingView>, IRequestHandler<ActivateListingCommand, bool>
{
    public const int MaxPlanChars = 600_000;
    public static readonly string[] Styles = ["modern", "classic", "najdi", "scandi", "luxury"];

    public async Task<ListingReply> Handle(CreateListingCommand cmd, CancellationToken ct)
    {
        var i = cmd.Input;
        var title = Clean(i.Title, 100);
        var phone = Clean(i.Phone, 30);
        if (title == "" || !IsPhone(phone)) return new ListingReply(false, null, null, "invalid");
        var planText = i.Plan ?? "";
        JsonElement plan;
        try { using var d = JsonDocument.Parse(planText.Length is > 0 and <= MaxPlanChars ? planText : "null"); plan = d.RootElement.Clone(); }
        catch (JsonException) { return new ListingReply(false, null, null, "plan"); }
        if (plan.ValueKind != JsonValueKind.Object || !plan.TryGetProperty("rooms", out var rooms) || rooms.ValueKind != JsonValueKind.Array || rooms.GetArrayLength() == 0
            || !plan.TryGetProperty("walls", out var walls) || walls.ValueKind != JsonValueKind.Array)
            return new ListingReply(false, null, null, "plan");
        if (await quota.TryConsumeAsync($"listing:ip:{me.Ip}", opt.Value.ListingsPerIp, ct) == null) return new ListingReply(false, null, null, "limit");

        var l = new Listing
        {
            Id = Ids.New(10), Title = title, City = Clean(i.City, 60), District = Clean(i.District, 60), Price = Clean(i.Price, 30), Phone = phone,
            Email = Email(i.Email), Lang = Langs.Of(i.Lang), Style = Styles.Contains(i.Style) ? i.Style! : "modern", Plan = planText, CreatedAt = clock.GetUtcNow(),
        };
        var list = rooms.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object).ToList();
        double Area(JsonElement r) => r.TryGetProperty("area", out var a) && a.TryGetDouble(out var v) ? v : 0;
        string Type(JsonElement r) => r.TryGetProperty("type", out var t) ? t.ToString() : "";
        l.Area = Math.Round(list.Sum(Area), 1);
        l.Copy = await CopyAsync(l, list, Area, Type, ct);
        db.Listings.Add(l);
        await db.SaveChangesAsync(ct);

        try
        {
            var tour = site.Value.Site(Langs.Prefix(l.Lang) + "/tour/?id=" + l.Id);
            var inv = await payments.CreateInvoiceAsync(new InvoiceRequest(Prices.Halalas(Prices.Listing), $"Oxira Design - 3D listing tour ({l.Id})", tour + "&paid=1", tour,
                new Dictionary<string, string> { ["kind"] = PaymentKinds.Listing, ["listing_id"] = l.Id }), ct);
            l.InvoiceId = inv.Id;
            await db.SaveChangesAsync(ct);
            return new ListingReply(true, l.Id, inv.Url);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // the draft stays; the owner can pay through WhatsApp and the team activates it
            log.LogError(e, "Moyasar invoice for listing {Id} failed", l.Id);
            return new ListingReply(true, l.Id, null);
        }
    }

    /// <summary>Claude's ad copy as JSON; on any failure an empty copy (the tour still works)</summary>
    private async Task<string> CopyAsync(Listing l, List<JsonElement> rooms, Func<JsonElement, double> area, Func<JsonElement, string> type, CancellationToken ct)
    {
        var empty = new JsonObject { ["ar"] = new JsonObject { ["headline"] = "", ["body"] = "" }, ["en"] = new JsonObject { ["headline"] = "", ["body"] = "" }, ["hashtags"] = new JsonArray() };
        int Count(string t) => rooms.Count(r => type(r) == t);
        var facts = string.Join("\n",
            "Title: " + l.Title, "City: " + (l.City == "" ? "-" : l.City), "District: " + (l.District == "" ? "-" : l.District),
            "Price: " + (l.Price == "" ? "on request" : l.Price + " SAR"), $"Total area: {l.Area.ToString(CultureInfo.InvariantCulture)} m2",
            $"Bedrooms: {Count("bedroom")}", $"Bathrooms: {Count("bath")}", $"Living rooms: {Count("living")}",
            "Kitchen: " + (Count("kitchen") > 0 ? "yes" : "not marked"), "Balcony: " + (Count("balcony") > 0 ? "yes" : "no"),
            "Rooms: " + string.Join(", ", rooms.Take(30).Select(r => (r.TryGetProperty("name", out var n) && n.ToString() != "" ? n.ToString() : type(r)) + " " + Math.Round(area(r)) + " m2")),
            "Interior style shown in the 3D tour: " + l.Style);
        try
        {
            var res = await ai.CreateMessageAsync(new AiRequest(
                "You write real-estate listing ads for Saudi property portals (Aqar, Bayut, Haraj) and social media. Use only the facts given; never invent amenities, prices, distances or features. Return only JSON, no markdown.",
                [new JsonObject { ["role"] = "user", ["content"] = "Write a listing ad for this property, which has an interactive 3D tour link.\n\n" + facts +
                    "\n\nReturn JSON exactly like {\"ar\":{\"headline\":\"...\",\"body\":\"...\"},\"en\":{\"headline\":\"...\",\"body\":\"...\"},\"hashtags\":[\"...\"]}. Arabic in clear Saudi-friendly Modern Standard Arabic. Headline under 70 characters. Body 60 to 110 words, short lines, ends with an invitation to open the 3D tour and contact on WhatsApp. 5 to 8 hashtags in Arabic and English without spaces." }],
                null, 1500, opt.Value.ListingModel, new JsonObject { ["thinking"] = new JsonObject { ["type"] = "disabled" } }), ct);
            var j = JsonNode.Parse(JsonObjectIn(res.Text) ?? "")!.AsObject();
            string S(JsonNode? n, int max) => Clip(n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "", max);
            var tags = (j["hashtags"] ?? j["ar"]?["hashtags"] ?? j["en"]?["hashtags"]) as JsonArray ?? [];
            var copy = new JsonObject
            {
                ["ar"] = new JsonObject { ["headline"] = S(j["ar"]?["headline"], 120), ["body"] = S(j["ar"]?["body"], 1500) },
                ["en"] = new JsonObject { ["headline"] = S(j["en"]?["headline"], 120), ["body"] = S(j["en"]?["body"], 1500) },
                ["hashtags"] = new JsonArray(tags.Take(10).Select(t => (JsonNode?)JsonValue.Create(S(t, 40).TrimStart('#'))).ToArray()),
            };
            return copy.ToJsonString();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Listing ad copy failed for {Id}", l.Id);
            return empty.ToJsonString();
        }
    }

    public async Task<ListingView> Handle(GetListingQuery q, CancellationToken ct)
    {
        var id = new string((q.Id ?? "").Where(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9').Take(20).ToArray());
        var l = await db.Listings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l == null || l.Status is not ("draft" or "active")) return new ListingView(false, Reason: "not_found");
        await db.Listings.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Views, x => x.Views + 1), ct);
        JsonElement copy;
        try { using var d = JsonDocument.Parse(l.Copy); copy = d.RootElement.Clone(); }
        catch (JsonException) { using var d = JsonDocument.Parse("{}"); copy = d.RootElement.Clone(); }
        var active = l.Status == "active";
        // drafts show as a preview without the owner's phone
        return new ListingView(true, l.Id, l.Status, l.Title, l.City, l.District, l.Price, l.Area, l.Style, l.Lang, active ? l.Phone : "", copy, l.Plan);
    }

    public async Task<bool> Handle(ActivateListingCommand cmd, CancellationToken ct)
    {
        var inv = cmd.Invoice;
        var id = inv.Metadata.GetValueOrDefault("listing_id") ?? "";
        if (id == "" || inv.Amount < Prices.Halalas(Prices.Listing)) return false;
        var now = clock.GetUtcNow();
        var changed = await db.Listings.Where(l => l.Id == id && l.InvoiceId == inv.Id && l.Status == "draft")
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, "active").SetProperty(l => l.PaidAt, now), ct);
        if (changed == 0) return false;
        var l = await db.Listings.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        foreach (var m in Templates.ListingLive(l, site.Value)) email.Enqueue(m);
        return true;
    }
}
