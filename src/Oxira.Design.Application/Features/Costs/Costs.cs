using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oxira.Design.Application.Common;
using Oxira.Design.Application.Emails;
using Oxira.Design.Domain;
using static Oxira.Design.Domain.Text;

namespace Oxira.Design.Application.Features.Costs;

public record CostRatesReply(bool Success, JsonElement? Rates, string? Updated, string? Reason = null) : IReply;
public record BuildQuoteInput(string? Name, string? Phone, string? Email, string? Country, string? City, double? Area, string? Level, string? Soil,
    double? Low, double? High, string? Currency, string? Plan, string? Notes, string? Lang, string? Page,
    [property: JsonPropertyName("company_website")] string? CompanyWebsite);

public record CostRatesQuery : IQuery<CostRatesReply>;
public record BuildQuoteCommand(BuildQuoteInput Input) : ICommand<IdReply>;
/// <summary>monthly: Claude with web search proposes new rates → a pending row and an approval email</summary>
public record ResearchCostsCommand : ICommand<int>;
public record ApproveRatesCommand(string Token) : ICommand<bool>;

public class CostHandlers(IAppDbContext db, TimeProvider clock, IQuota quota, IAnthropic ai, IEmailSender email, ICurrentUser me,
    IOptions<FormLimitOptions> forms, IOptions<JobOptions> jobs, IOptions<SiteOptions> site, ILogger<CostHandlers> log)
    : IRequestHandler<CostRatesQuery, CostRatesReply>, IRequestHandler<BuildQuoteCommand, IdReply>, IRequestHandler<ResearchCostsCommand, int>,
      IRequestHandler<ApproveRatesCommand, bool>
{
    /// <summary>starting point when no rates were approved yet (the site's bundled rates, October 2026)</summary>
    public const string Seed = """
        {"updated":"2026-10","SA":{"shell":[550,800],"finish":{"economy":[600,900],"standard":[900,1400],"luxury":[1600,2800]},"steelTon":[3100,3300]},
         "EG":{"shell":[3800,5000],"finish":{"economy":[3500,5000],"standard":[5000,7500],"luxury":[7500,12000]},"steelTon":[37500,39800],"cementTon":[3850,4010]}}
        """;

    /// <summary>a value moves at most this much a month without a human looking at it twice</summary>
    public const double MaxMove = 0.35;

    public async Task<CostRatesReply> Handle(CostRatesQuery q, CancellationToken ct)
    {
        var row = await db.CostRates.AsNoTracking().Where(r => r.Status == "active").OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
        if (row == null) return new CostRatesReply(false, null, null, "not_found");
        try
        {
            using var d = JsonDocument.Parse(row.Rates);
            var rates = d.RootElement.Clone();
            return new CostRatesReply(true, rates, rates.TryGetProperty("updated", out var u) ? u.ToString() : row.Updated);
        }
        catch (JsonException) { return new CostRatesReply(false, null, null, "not_found"); }
    }

    public async Task<IdReply> Handle(BuildQuoteCommand cmd, CancellationToken ct)
    {
        var i = cmd.Input;
        if (!string.IsNullOrEmpty(i.CompanyWebsite)) return new IdReply(false, null, "invalid");
        var name = Clean(i.Name, 120);
        var phone = new string((i.Phone ?? "").Where(ch => char.IsAsciiDigit(ch) || ch == '+').Take(20).ToArray());
        if (name.Length < 2 || Digits(phone).Length < 8) return new IdReply(false, null, "invalid");
        var ipKey = $"buildquote:ip:{me.Ip}";
        if (await quota.TryConsumeAsync(ipKey, forms.Value.BuildQuotesPerIp, ct) == null) return new IdReply(false, null, "limit");
        if (await quota.TryConsumeAsync("buildquote:all", forms.Value.BuildQuotesPerDay, ct) == null)
        {
            await quota.RefundAsync(ipKey, ct);
            return new IdReply(false, null, "limit");
        }
        static long R(double? v) => v is { } x && double.IsFinite(x) ? (long)Math.Round(Math.Clamp(x, 0, 1e12)) : 0;
        var country = i.Country == "EG" ? "EG" : "SA";
        var q = new BuildLead
        {
            Name = name, Phone = phone, Email = Email(i.Email), Country = country, City = Clean(i.City, 40), Area = (int)Math.Min(R(i.Area), 1_000_000),
            Level = Clean(i.Level, 20), Soil = Clean(i.Soil, 20), Low = R(i.Low), High = R(i.High), Currency = country == "EG" ? "EGP" : "SAR",
            Plan = Clean(i.Plan, 80), Notes = Clean(i.Notes, 2000), Lang = Langs.Of(i.Lang), Page = Clean(i.Page, 600), CreatedAt = clock.GetUtcNow(),
        };
        db.BuildLeads.Add(q);
        await db.SaveChangesAsync(ct);

        var levels = new Dictionary<string, string> { ["economy"] = "اقتصادي", ["standard"] = "متوسط", ["luxury"] = "فاخر" };
        string Nf(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
        var rows = new (string, string?)[]
        {
            ("رقم الطلب", "#" + q.Id), ("الاسم", q.Name), ("الجوال", q.Phone), ("البريد", q.Email), ("الدولة / المدينة", q.Country + " / " + q.City),
            ("المساحة المبنية", q.Area + " م²"), ("مستوى التشطيب", levels.GetValueOrDefault(q.Level, q.Level)), ("التربة", q.Soil),
            ("التقدير", $"{Nf(q.Low)} – {Nf(q.High)} {q.Currency}"), ("المخطط", q.Plan), ("ملاحظات", q.Notes), ("اللغة", q.Lang), ("الصفحة", q.Page),
        };
        email.Enqueue(new EmailMessage([site.Value.TeamEmail], $"طلب عروض بناء #{q.Id} · {q.City} · {q.Area} م²",
            Templates.TeamShell("طلب عروض مقاولين للبناء", Templates.Table(rows) + Templates.P("من حاسبة تكلفة البناء في design.oxira.sa/cost/ — جدول BuildLeads.", "color:#556779;font-size:13px")),
            q.Email == "" ? null : q.Email));
        return new IdReply(true, q.Id);
    }

    public async Task<int> Handle(ResearchCostsCommand cmd, CancellationToken ct)
    {
        var active = await db.CostRates.AsNoTracking().Where(r => r.Status == "active").OrderByDescending(r => r.Id).Select(r => r.Rates).FirstOrDefaultAsync(ct);
        var current = (JsonNode.Parse(active ?? Seed) as JsonObject) ?? (JsonObject)JsonNode.Parse(Seed)!;
        JsonObject Pick(string c) => new()
        {
            ["shell"] = current[c]?["shell"]?.DeepClone(), ["finish"] = current[c]?["finish"]?.DeepClone(),
            ["steelTon"] = current[c]?["steelTon"]?.DeepClone(), ["cementTon"] = current[c]?["cementTon"]?.DeepClone(),
        };
        var now = Riyadh.Now(clock);
        var system = "You keep a construction-cost calculator up to date. Use web search to find prices published in the last few months for building private villas. " +
            "Values are per square metre of built area, materials and labour included: shell = structure with materials (excavation, foundations, reinforced concrete frame, slabs, block/brick walls); " +
            "finish = finishing work added on top of the shell (plaster, MEP, tiles, paint, doors, windows, kitchens, bathrooms) at three levels. Also steel rebar price per ton and (Egypt) cement per ton. " +
            "Saudi Arabia in SAR, Egypt in EGP. Prefer several independent recent sources (contractors, price bulletins, official statistics) and give ranges [low, high] as integers. " +
            "If you cannot find reliable newer data for a value, return the current value unchanged. Reply with ONE JSON object only, no prose: " +
            "{\"SA\": {\"shell\": [lo, hi], \"finish\": {\"economy\": [lo, hi], \"standard\": [lo, hi], \"luxury\": [lo, hi]}, \"steelTon\": [lo, hi]}, " +
            "\"EG\": {\"shell\": [lo, hi], \"finish\": {\"economy\": [lo, hi], \"standard\": [lo, hi], \"luxury\": [lo, hi]}, \"steelTon\": [lo, hi], \"cementTon\": [lo, hi]}, " +
            "\"notes\": \"two or three sentences in Arabic on what changed and why\", \"sources\": [{\"title\": \"...\", \"url\": \"https://...\"}]}";
        var user = $"Month: {now.ToString("MMMM yyyy", CultureInfo.InvariantCulture)}. Current values: " + new JsonObject { ["SA"] = Pick("SA"), ["EG"] = Pick("EG") }.ToJsonString();
        var tools = new JsonArray { new JsonObject { ["type"] = "web_search_20250305", ["name"] = "web_search", ["max_uses"] = 10 } };
        var res = await ai.CreateMessageAsync(new AiRequest(system, [new JsonObject { ["role"] = "user", ["content"] = user }], tools, 16000, jobs.Value.ResearchModel), ct);
        JsonObject proposal;
        try { proposal = JsonNode.Parse(JsonObjectIn(res.Text) ?? "{}") as JsonObject ?? []; }
        catch (JsonException) { proposal = []; }

        var (next, changed, skipped) = Merge(current, proposal);
        next["updated"] = now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var sources = (proposal["sources"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(s => (s["url"] as JsonValue)?.TryGetValue<string>(out var u) == true && (u.StartsWith("https://") || u.StartsWith("http://")))
            .Take(12).Select(s => new JsonObject { ["title"] = Clip(s["title"]?.ToString(), 200), ["url"] = Clip(s["url"]!.ToString(), 500) }).ToList();
        var notes = Clip((proposal["notes"] as JsonValue)?.TryGetValue<string>(out var n) == true ? n : "", 1500);
        var row = new CostRate
        {
            Rates = next.ToJsonString(), Status = "pending", Updated = (string)next["updated"]!, Token = Ids.Hex(32), Notes = notes,
            Sources = Clip(new JsonArray(sources.Select(s => (JsonNode?)s).ToArray()).ToJsonString(), 4000), CreatedAt = clock.GetUtcNow(),
        };
        db.CostRates.Add(row);
        await db.SaveChangesAsync(ct);
        email.Enqueue(Templates.CostProposal(row, changed, skipped, sources, site.Value.Api("/api/costs/approve?t=" + row.Token), site.Value.TeamEmail));
        log.LogInformation("Cost research: {Changed} changes, {Skipped} skipped", changed.Count, skipped.Count);
        return changed.Count;
    }

    public record Change(string Label, long[] From, long[] To);

    /// <summary>
    /// Takes a proposed value only when it is a sane [low, high] range whose midpoint moves at most 35%; anything else keeps the
    /// current value and is listed for review.
    /// </summary>
    public static (JsonObject Next, List<Change> Changed, List<string> Skipped) Merge(JsonObject current, JsonObject proposal)
    {
        var next = (JsonObject)current.DeepClone();
        var changed = new List<Change>();
        var skipped = new List<string>();
        static long[]? Range(JsonNode? n)
        {
            if (n is not JsonArray { Count: 2 } a) return null;
            var v = new double[2];
            for (var k = 0; k < 2; k++)
                if (a[k] is not JsonValue x || !x.TryGetValue<double>(out v[k]) || !double.IsFinite(v[k]) || v[k] <= 0) return null;
            return v[0] <= v[1] ? [(long)Math.Round(v[0]), (long)Math.Round(v[1])] : null;
        }
        static JsonNode? Get(JsonNode? n, string k) => n is JsonObject o ? o[k] : null;
        void Take(string country, string[] path, string label)
        {
            JsonNode? cur = current[country], prop = proposal[country];
            foreach (var p in path[..^1]) { cur = Get(cur, p); prop = Get(prop, p); }
            var key = path[^1];
            var oldR = Range(Get(cur, key));
            if (oldR == null) return;
            var newR = Range(Get(prop, key));
            if (newR == null) { skipped.Add(label + ": no usable value"); return; }
            var move = Math.Abs((newR[0] + newR[1]) / (double)(oldR[0] + oldR[1]) - 1);
            if (move > MaxMove) { skipped.Add($"{label}: change of {Math.Round(move * 100)}% not taken ({newR[0]}–{newR[1]})"); return; }
            JsonNode target = next[country]!;
            foreach (var p in path[..^1]) target = target[p]!;
            if (newR[0] != oldR[0] || newR[1] != oldR[1]) changed.Add(new Change(label, oldR, newR));
            target[key] = new JsonArray(newR[0], newR[1]);
        }
        foreach (var c in new[] { "SA", "EG" })
        {
            Take(c, ["shell"], c + " shell");
            foreach (var l in new[] { "economy", "standard", "luxury" }) Take(c, ["finish", l], $"{c} finish {l}");
            Take(c, ["steelTon"], c + " steel/ton");
        }
        Take("EG", ["cementTon"], "EG cement/ton");
        return (next, changed, skipped);
    }

    public async Task<bool> Handle(ApproveRatesCommand cmd, CancellationToken ct)
    {
        var t = cmd.Token ?? "";
        if (t.Length != 32 || !t.All(Uri.IsHexDigit)) return false;
        return await db.CostRates.Where(r => r.Token == t && r.Status == "pending")
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "active").SetProperty(r => r.Token, ""), ct) > 0;
    }
}
