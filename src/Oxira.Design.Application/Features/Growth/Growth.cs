using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oxira.Design.Application.Common;
using Oxira.Design.Application.Emails;
using Oxira.Design.Application.Features.Costs;
using Oxira.Design.Domain;
using static Oxira.Design.Domain.Text;

namespace Oxira.Design.Application.Features.Growth;

/// <summary>Daily 10:00 Riyadh: saved-plan follow-ups, rating requests, new-order alerts to designers. Returns the emails sent.</summary>
public record DailyGrowthCommand : ICommand<int>;
/// <summary>Sunday 09:00 Riyadh: numbers of the week + "plan of the week" social captions by Claude, to the team.</summary>
public record WeeklyReportCommand : ICommand<bool>;
public record OptOutCommand(string? Email, string? Token) : ICommand<bool>;
/// <summary>Runs the scheduled jobs that are due now and have not run for their period yet. Returns the keys that ran.</summary>
public record RunDueJobsCommand : ICommand<List<string>>;

/// <summary>Signed unsubscribe links (HMAC of the address). Links from the old n8n workflow (unsigned FNV hash) work only until Jobs:LegacyOptOutUntil.</summary>
public static class OptOutLinks
{
    public static string Sign(string email, string secret)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("optout:" + email.Trim().ToLowerInvariant()));
        return Convert.ToBase64String(mac, 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Legacy(string email)
    {
        uint h = 2166136261;
        foreach (var ch in "oxira-optout:" + email) h = (h ^ ch) * 16777619;
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var sb = new StringBuilder();
        do { sb.Insert(0, digits[(int)(h % 36)]); h /= 36; } while (h > 0);
        return sb.ToString();
    }

    /// <param name="allowLegacy">the unsigned n8n hashes are accepted only until Jobs:LegacyOptOutUntil (anyone could compute them)</param>
    public static bool Verify(string email, string token, string secret, bool allowLegacy) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(email, secret)), Encoding.UTF8.GetBytes(token)) || (allowLegacy && token == Legacy(email));

    public static string Link(SiteOptions site, string email, string secret) =>
        site.Api($"/api/optout?e={Uri.EscapeDataString(email)}&t={Sign(email, secret)}");
}

public class GrowthHandlers(IAppDbContext db, TimeProvider clock, IEmailSender email, IAnthropic ai, ISender mediator,
    IOptions<JobOptions> jobs, IOptions<SiteOptions> site, ILogger<GrowthHandlers> log)
    : IRequestHandler<DailyGrowthCommand, int>, IRequestHandler<WeeklyReportCommand, bool>, IRequestHandler<OptOutCommand, bool>,
      IRequestHandler<RunDueJobsCommand, List<string>>
{
    private static readonly string[] AlertStatuses = [OrderStatuses.New, OrderStatuses.Paid];

    public async Task<int> Handle(DailyGrowthCommand cmd, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var s = site.Value;
        var secret = jobs.Value.OptOutSecret;
        var off = (await db.OptOuts.AsNoTracking().Select(o => o.Email).ToListAsync(ct)).ToHashSet();
        var sent = 0;

        // 1. a plan saved 2–14 days ago by someone who never ordered: one follow-up per owner
        var from = now.AddDays(-14);
        var to = now.AddDays(-2);
        var plans = await db.Projects.AsNoTracking().Where(p => !p.Nudged && p.UpdatedAt >= from && p.UpdatedAt <= to)
            .OrderByDescending(p => p.UpdatedAt).ToListAsync(ct);
        var owners = plans.Select(p => p.Owner).Distinct().ToList();
        var ordered = (await db.Orders.AsNoTracking().Where(o => owners.Contains(o.Email)).Select(o => o.Email).Distinct().ToListAsync(ct)).ToHashSet();
        foreach (var p in plans.GroupBy(p => p.Owner).Select(g => g.First()))
        {
            if (!IsEmail(p.Owner) || off.Contains(p.Owner) || ordered.Contains(p.Owner)) continue;
            email.Enqueue(Templates.PlanNudge(p, s, OptOutLinks.Link(s, p.Owner, secret)));
            await db.Projects.Where(x => x.Owner == p.Owner).ExecuteUpdateAsync(u => u.SetProperty(x => x.Nudged, true), ct);
            sent++;
        }

        // 2. delivered 3–30 days ago and not rated: ask once
        var dFrom = now.AddDays(-30);
        var dTo = now.AddDays(-3);
        var delivered = await db.Orders.AsNoTracking().Where(o => o.DeliveredAt != null && o.DeliveredAt >= dFrom && o.DeliveredAt <= dTo && o.Rating == 0 && !o.ReviewAsked)
            .ToListAsync(ct);
        foreach (var o in delivered)
        {
            if (!IsEmail(o.Email) || off.Contains(o.Email)) continue;
            email.Enqueue(Templates.RatingRequest(o, s, OptOutLinks.Link(s, o.Email, secret)));
            await db.Orders.Where(x => x.Id == o.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ReviewAsked, true), ct);
            sent++;
        }

        // 3. new orders without a designer (last 3 days): tell the active designers once (bcc)
        var designers = await db.Designers.AsNoTracking().Where(d => d.Status == DesignerStatuses.Active).Select(d => d.Email).ToListAsync(ct);
        if (designers.Count > 0)
        {
            var since = now.AddDays(-3);
            var fresh = await db.Orders.AsNoTracking().Where(o => o.Designer == null && !o.NotifiedDesigners && AlertStatuses.Contains(o.Status) && o.CreatedAt >= since)
                .ToListAsync(ct);
            foreach (var o in fresh)
            {
                email.Enqueue(Templates.DesignerAlert(o, s, designers));
                await db.Orders.Where(x => x.Id == o.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.NotifiedDesigners, true), ct);
                sent++;
            }
        }
        log.LogInformation("Daily growth emails: {Sent}", sent);
        return sent;
    }

    private static readonly string[] WeeklyPlans =
    [
        "villa-20x30-4-bedrooms", "villa-20x25-5-bedrooms", "villa-15x30-4-bedrooms", "house-15x30-3-bedrooms", "villa-25x30-5-bedrooms",
        "apartment-building-20x30-2-bedroom-units", "villa-30x30-6-bedrooms", "house-25x25-3-bedrooms", "villa-20x20-4-bedrooms",
        "apartment-building-25x35-3-bedroom-units", "villa-15x25-4-bedrooms", "house-30x30-4-bedrooms",
    ];

    public static string Describe(string slug)
    {
        var m = System.Text.RegularExpressions.Regex.Match(slug, @"^(.+?)-(\d+)x(\d+)-(\d)");
        if (!m.Success) return slug;
        var kind = m.Groups[1].Value;
        return (kind == "apartment-building" ? "apartment building" : kind == "house" ? "single-storey house" : "two-storey villa") +
               $" on a {m.Groups[2].Value} x {m.Groups[3].Value} m plot, {m.Groups[4].Value}" + (kind == "apartment-building" ? "-bedroom apartments" : " bedrooms");
    }

    public async Task<bool> Handle(WeeklyReportCommand cmd, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var week = now.AddDays(-7);
        var rated = await db.Orders.AsNoTracking().Where(o => o.Rating > 0).Select(o => o.Rating).ToListAsync(ct);
        var n = new Templates.WeekNumbers(
            await db.Projects.CountAsync(p => p.CreatedAt >= week, ct), await db.Projects.CountAsync(ct), await db.Projects.CountAsync(p => p.Public, ct),
            await db.Orders.CountAsync(o => o.CreatedAt >= week, ct), await db.Orders.CountAsync(ct), await db.Orders.CountAsync(o => o.DeliveredAt >= week, ct),
            await db.Orders.CountAsync(o => o.Designer == null && AlertStatuses.Contains(o.Status), ct),
            rated.Count > 0 ? Math.Round(rated.Average(), 1) : 0, rated.Count,
            await db.Designers.CountAsync(d => d.Status == DesignerStatuses.Active, ct), await db.Designers.CountAsync(d => d.Status == DesignerStatuses.Pending, ct),
            await db.Waitlist.CountAsync(w => w.CreatedAt >= week, ct), await db.Waitlist.CountAsync(ct));

        var slug = WeeklyPlans[(int)(now.ToUnixTimeMilliseconds() / (7L * 86400000) % WeeklyPlans.Length)];
        var desc = Describe(slug);
        var captions = new Dictionary<string, string>();
        try
        {
            var res = await ai.CreateMessageAsync(new AiRequest(
                "You write social media posts for Oxira Design (design.oxira.sa), a free online tool that generates editable floor plans from a plot size, with 3D, DXF and IFC export, made in Saudi Arabia for the Gulf and the world. Reply with ONE JSON object only: {\"instagram_ar\": \"...\", \"instagram_en\": \"...\", \"pinterest_title_en\": \"...\", \"pinterest_desc_en\": \"...\", \"tiktok_hook_ar\": \"...\", \"x_en\": \"...\"}. Instagram posts: 3-5 short lines, a question to the audience, 5-8 relevant hashtags. Arabic in Gulf dialect. Pinterest title under 100 characters with the plot size. TikTok hook: one spoken sentence for the first 2 seconds of a screen recording. X post under 260 characters. Mention the link once in each.",
                [new JsonObject { ["role"] = "user", ["content"] = $"Plan of the week: {desc}. Link: https://design.oxira.sa/en/plans/{slug}/ (Arabic page: https://design.oxira.sa/plans/{slug}/)." }],
                null, 6000, jobs.Value.CaptionsModel), ct);
            if (JsonNode.Parse(JsonObjectIn(res.Text) ?? "{}") is JsonObject o)
                foreach (var (k, v) in o) if (v is JsonValue jv && jv.TryGetValue<string>(out var str)) captions[k] = Clip(str, 2000);
        }
        catch (Exception e) when (e is not OperationCanceledException and not JsonException) { log.LogWarning(e, "Weekly captions failed"); }
        catch (JsonException) { /* report without captions */ }

        email.Enqueue(Templates.WeeklyReport(n, slug, desc, captions, site.Value.TeamEmail));
        return true;
    }

    private bool LegacyAllowed() =>
        DateOnly.TryParse(jobs.Value.LegacyOptOutUntil, CultureInfo.InvariantCulture, out var until) && Riyadh.Today(clock) <= until;

    public async Task<bool> Handle(OptOutCommand cmd, CancellationToken ct)
    {
        var mail = Clip(cmd.Email, 160).ToLowerInvariant();
        if (mail == "" || string.IsNullOrEmpty(cmd.Token) || !OptOutLinks.Verify(mail, cmd.Token, jobs.Value.OptOutSecret, LegacyAllowed())) return false;
        if (!await db.OptOuts.AnyAsync(o => o.Email == mail, ct))
        {
            db.OptOuts.Add(new OptOut { Email = mail, Reason = "link", CreatedAt = clock.GetUtcNow() });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { /* the same link opened twice at once */ }
        }
        return true;
    }

    /// <summary>
    /// daily: from DailyHour on each day; weekly: Sundays from WeeklyHour; monthly: from MonthlyHour on the 1st (caught up until the 3rd).
    /// A JobRuns row per period makes each run once, even with several API instances.
    /// </summary>
    public static List<(string Key, object Job)> Due(DateTimeOffset nowRiyadh, JobOptions o)
    {
        var due = new List<(string, object)>();
        var day = nowRiyadh.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (nowRiyadh.Hour >= o.DailyHour) due.Add(("daily:" + day, new DailyGrowthCommand()));
        if (nowRiyadh.DayOfWeek == DayOfWeek.Sunday && nowRiyadh.Hour >= o.WeeklyHour) due.Add(("weekly:" + day, new WeeklyReportCommand()));
        if (nowRiyadh.Day is >= 1 and <= 3 && (nowRiyadh.Day > 1 || nowRiyadh.Hour >= o.MonthlyHour))
            due.Add(("monthly:" + nowRiyadh.ToString("yyyy-MM", CultureInfo.InvariantCulture), new ResearchCostsCommand()));
        return due;
    }

    public async Task<List<string>> Handle(RunDueJobsCommand cmd, CancellationToken ct)
    {
        var ran = new List<string>();
        foreach (var (key, job) in Due(Riyadh.Now(clock), jobs.Value))
        {
            if (await db.JobRuns.AnyAsync(j => j.Key == key, ct)) continue;
            var row = new JobRun { Key = key, RanAt = clock.GetUtcNow() };
            db.JobRuns.Add(row);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                // another instance took it
                db.JobRuns.Entry(row).State = EntityState.Detached;
                continue;
            }
            try
            {
                await mediator.Send(job, ct);
                ran.Add(key);
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Job {Key} failed", key); }
        }
        return ran;
    }
}
