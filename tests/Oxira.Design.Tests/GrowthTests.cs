using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Oxira.Design.Application.Common;
using Oxira.Design.Application.Features.Costs;
using Oxira.Design.Application.Features.Growth;
using Oxira.Design.Domain;

namespace Oxira.Design.Tests;

public class ListingTests(TestApp app) : IClassFixture<TestApp>
{
    private static string Plan() => JsonSerializer.Serialize(new
    {
        v = 1, walls = Array.Empty<object>(),
        rooms = new object[] { new { name = "Living", type = "living", area = 30.4 }, new { name = "", type = "bedroom", area = 14 }, new { name = "Bath", type = "bath", area = 5 } },
    });

    [Fact]
    public async Task Listing_draft_then_paid_and_live()
    {
        app.Ai.Script.Enqueue(FakeAnthropic.Text("```json\n{\"ar\":{\"headline\":\"شقة في الملقا\",\"body\":\"جولة ثلاثية الأبعاد\"},\"en\":{\"headline\":\"Apartment in Al Malqa\",\"body\":\"Open the 3D tour\"},\"hashtags\":[\"#عقار\",\"RiyadhHomes\"]}\n```"));
        var (status, body) = await app.Client().PostJson("/api/listings", new { title = "شقة في الملقا", city = "Riyadh", district = "Al Malqa", price = 850000, phone = "+966 50 000 0000", email = "owner@example.com", lang = "en", style = "luxury", plan = Plan() });
        Assert.Equal(HttpStatusCode.OK, status);
        var id = body.Str("id");
        Assert.Equal(10, id.Length);
        Assert.StartsWith("https://checkout.moyasar.com/", body.Str("payUrl"));
        var req = app.Payments.Requests.Last();
        Assert.Equal(4900, req.Amount);
        Assert.Equal($"https://design.oxira.sa/en/tour/?id={id}&paid=1", req.SuccessUrl);
        var ask = app.Ai.Calls.Last();
        Assert.Equal("claude-haiku-5-5", ask.Model);
        Assert.Contains("Bedrooms: 1", (string?)ask.Messages[0]!["content"]);
        Assert.Equal("disabled", (string?)ask.Extra!["thinking"]!["type"]);

        var (_, draft) = await app.Client().GetJson($"/api/listings/{id}");
        Assert.Equal("draft", draft.Str("status"));
        Assert.Equal("", draft.Str("phone"));
        Assert.Equal("Apartment in Al Malqa", draft.GetProperty("copy").GetProperty("en").Str("headline"));
        Assert.Equal("عقار", draft.GetProperty("copy").GetProperty("hashtags")[0].GetString());
        Assert.Equal(49.4, draft.GetProperty("area").GetDouble());
        Assert.Contains("\"walls\"", draft.Str("plan"));

        var inv = app.Payments.Last("listing");
        app.Payments.Pay(inv);
        await app.Client().PostJson("/api/payments/moyasar/callback", new { id = inv });
        var (_, live) = await app.Client().GetJson($"/api/listings/{id}");
        Assert.Equal("active", live.Str("status"));
        Assert.Equal("+966 50 000 0000", live.Str("phone"));
        var mail = app.Email.To("owner@example.com").Single();
        Assert.Contains("api.qrserver.com", mail.Html);
        Assert.Contains($"/en/tour/?id={id}", mail.Html);
        Assert.True(await app.Db(db => db.Listings.Where(l => l.Id == id).Select(l => l.Views).FirstAsync()) >= 2);

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client().GetJson("/api/listings/nothere123")).Status);
    }

    [Fact]
    public async Task Listing_refusals_and_ai_failure()
    {
        var (status, body) = await app.Client().PostJson("/api/listings", new { title = "x", phone = "123", plan = Plan() });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid", body.Str("reason"));
        (_, body) = await app.Client().PostJson("/api/listings", new { title = "Villa", phone = "0500000000", plan = "{\"rooms\":[]}" });
        Assert.Equal("plan", body.Str("reason"));

        app.Ai.Fail = true;
        try
        {
            (status, body) = await app.Client().PostJson("/api/listings", new { title = "Villa", phone = "0500000000", plan = Plan() });
            Assert.Equal(HttpStatusCode.OK, status);
            var (_, l) = await app.Client().GetJson($"/api/listings/{body.Str("id")}");
            Assert.Equal("", l.GetProperty("copy").GetProperty("ar").Str("headline"));
        }
        finally { app.Ai.Fail = false; }
    }
}

public class FormTests(TestApp app) : IClassFixture<TestApp>
{
    private static object Finishing(string name = "Ahmed") => new
    {
        name, phone = "+966500000000", email = "fin@example.com", city = "Riyadh", level = "premium", notes = "", lang = "ar", file = "plan.dxf", area = 120.44,
        boq = new { floor = 132, paint = 310, tiles = 60, ceiling = 120, skirting = 95 }, rooms = new[] { new { name = "Living", type = "living", area = 30 } },
    };

    [Fact]
    public async Task Finishing_quotes_are_saved_and_limited()
    {
        var (status, body) = await app.Client().PostJson("/api/finishing", Finishing());
        Assert.Equal(HttpStatusCode.OK, status);
        var id = body.Int("id");
        var lead = await app.Db(db => db.Leads.SingleAsync(l => l.Id == id));
        Assert.Equal("finishing", lead.Package);
        Assert.Equal("studio", lead.Channel);
        Assert.Contains("Premium", lead.Needs);
        Assert.Contains("\"skirting\":95", lead.Context);
        var mail = app.Email.To("info@oxira.sa").Single(m => m.Subject.EndsWith($"#{id}"));
        Assert.Equal("fin@example.com", mail.ReplyTo);
        Assert.Contains("120.4 m2", mail.Subject);

        (status, body) = await app.Client().PostJson("/api/finishing", new { name = "x", phone = "0500000000", area = 10 });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid", body.Str("reason"));

        await app.Client().PostJson("/api/finishing", Finishing("B"));
        await app.Client().PostJson("/api/finishing", Finishing("C"));
        (status, body) = await app.Client().PostJson("/api/finishing", Finishing("D"));
        Assert.Equal((HttpStatusCode)429, status);
        Assert.Equal("limit", body.Str("reason"));
    }

    [Fact]
    public async Task Build_quotes_are_saved_and_limited()
    {
        object Q(string name, string? hp = null) => new { name, phone = "05 0000 0000", country = "EG", city = "cairo", area = 400, level = "standard", soil = "good", low = 638000.4, high = 986000, plan = "villa", lang = "ar", page = "https://design.oxira.sa/cost/", company_website = hp };
        var (status, body) = await app.Client().PostText("/api/costs/quotes", Q("Ahmed"));
        Assert.Equal(HttpStatusCode.OK, status);
        var lead = await app.Db(db => db.BuildLeads.SingleAsync(l => l.Id == body.Int("id")));
        Assert.Equal("EGP", lead.Currency);
        Assert.Equal(638000, lead.Low);
        Assert.Contains(app.Email.To("info@oxira.sa"), m => m.Subject.StartsWith($"طلب عروض بناء #{lead.Id} "));

        Assert.Equal("invalid", (await app.Client().PostText("/api/costs/quotes", Q("Ahmed", "http://spam"))).Body.Str("reason"));
        Assert.Equal("invalid", (await app.Client().PostText("/api/costs/quotes", new { name = "Ahmed", phone = "123" })).Body.Str("reason"));
        for (var n = 0; n < 4; n++) Assert.Equal(HttpStatusCode.OK, (await app.Client().PostText("/api/costs/quotes", Q("Q" + n))).Status);
        (status, body) = await app.Client().PostText("/api/costs/quotes", Q("Late"));
        Assert.Equal((HttpStatusCode)429, status);
        Assert.Equal("limit", body.Str("reason"));
    }

    [Fact]
    public async Task Monthly_research_proposes_rates_that_go_live_once_approved()
    {
        var (s0, b0) = await app.Client().GetJson("/api/costs/rates");
        Assert.Equal(HttpStatusCode.NotFound, s0);
        Assert.False(b0.Bool("success"));

        app.Ai.Script.Enqueue(FakeAnthropic.Text("Here you go: {\"SA\": {\"shell\": [600, 850], \"finish\": {\"economy\": [2000, 3000], \"standard\": [900, 1400], \"luxury\": \"n/a\"}, \"steelTon\": [3150, 3350]}, " +
            "\"EG\": {\"shell\": [3800, 5000], \"finish\": [1, 2]}, \"notes\": \"ارتفاع طفيف في أسعار الحديد\", \"sources\": [{\"title\": \"Bulletin\", \"url\": \"https://example.com/prices\"}, {\"title\": \"bad\", \"url\": \"javascript:alert(1)\"}]}"));
        var changes = await app.Send(new ResearchCostsCommand());
        Assert.Equal(2, changes); // SA shell and SA steel; economy moved too much, luxury and EG finish were unusable
        var ask = app.Ai.Calls.Last();
        Assert.Equal("web_search", (string?)ask.Tools![0]!["name"]);
        Assert.Contains("\"shell\":[550,800]", (string?)ask.Messages[0]!["content"]);

        var mail = app.Email.To("info@oxira.sa").Single(m => m.Subject.StartsWith("اعتماد أسعار البناء لشهر 2026-10 (2 تغيير)"));
        Assert.Contains("change of", mail.Html);
        Assert.Contains("https://example.com/prices", mail.Html);
        Assert.DoesNotContain("javascript:", mail.Html);
        var link = new Uri(Regex.Match(mail.Html, "href=\"([^\"]*/api/costs/approve\\?t=[a-f0-9]{32})\"").Groups[1].Value);

        // nothing changes on the site before approval
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client().GetJson("/api/costs/rates")).Status);
        var page = await app.Client().GetAsync(link.PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("تم اعتماد", await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client().GetAsync(link.PathAndQuery)).StatusCode);

        var res = await app.Client().GetAsync("/api/costs/rates");
        Assert.Equal("public, max-age=3600", res.Headers.CacheControl!.ToString());
        var rates = (await Http.Read(res)).GetProperty("rates");
        Assert.Equal(600, rates.GetProperty("SA").GetProperty("shell")[0].GetInt32());
        Assert.Equal(600, rates.GetProperty("SA").GetProperty("finish").GetProperty("economy")[0].GetInt32());
        Assert.Equal(3850, rates.GetProperty("EG").GetProperty("cementTon")[0].GetInt32());
        Assert.Equal("2026-10", rates.Str("updated"));
    }

    [Fact]
    public void Merge_keeps_values_that_move_too_much()
    {
        var cur = JsonNode.Parse(CostHandlers.Seed)!.AsObject();
        var (next, changed, skipped) = CostHandlers.Merge(cur, JsonNode.Parse("{\"SA\": {\"shell\": [800, 1200]}}")!.AsObject());
        Assert.Empty(changed);
        Assert.Contains(skipped, s => s.StartsWith("SA shell: change of"));
        Assert.Equal(550, (int)next["SA"]!["shell"]![0]!);
    }
}

public class GrowthTests(TestApp app) : IClassFixture<TestApp>
{
    private async Task<string> ProjectAsync(string owner, double daysAgo, string lang = "ar")
    {
        var (_, saved) = await app.Client(owner).PostJson("/api/projects", new { title = "Villa 20x30", kind = "plan", data = "{}", lang });
        var pid = saved.Str("pid");
        var at = app.Clock.GetUtcNow().AddDays(-daysAgo);
        await app.Db(db => db.Projects.Where(p => p.Pid == pid).ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedAt, at)));
        return pid;
    }

    [Fact]
    public void Schedule_follows_riyadh_time()
    {
        var o = new JobOptions();
        DateTimeOffset At(int y, int m, int d, int h) => new(y, m, d, h, 0, 0, TimeSpan.FromHours(3));
        Assert.Empty(GrowthHandlers.Due(At(2026, 10, 13, 9), o));
        Assert.Equal(new[] { "daily:2026-10-13" }, GrowthHandlers.Due(At(2026, 10, 13, 10), o).Select(d => d.Key));
        Assert.Equal(new[] { "weekly:2026-10-18" }, GrowthHandlers.Due(At(2026, 10, 18, 9), o).Select(d => d.Key)); // Sunday, before the daily hour
        Assert.Equal(new[] { "daily:2026-11-01", "weekly:2026-11-01", "monthly:2026-11" }, GrowthHandlers.Due(At(2026, 11, 1, 10), o).Select(d => d.Key));
        Assert.Contains("monthly:2026-11", GrowthHandlers.Due(At(2026, 11, 3, 2), o).Select(d => d.Key));
        Assert.DoesNotContain("monthly:2026-11", GrowthHandlers.Due(At(2026, 11, 4, 12), o).Select(d => d.Key));
    }

    [Fact]
    public async Task Daily_follow_ups_rating_requests_designer_alerts_and_opt_out()
    {
        await ProjectAsync("nudge.me@example.com", 5, "en");
        await ProjectAsync("too.new@example.com", 1);
        await ProjectAsync("too.old@example.com", 20);
        await ProjectAsync("ordered@example.com", 5);
        await app.Client().PostForm("/api/orders", OrderTests.Form(OrderTests.Fields("plan", "ordered@example.com")));
        await ProjectAsync("legacy.optout@example.com", 6);
        var page = await app.Client().GetAsync($"/api/optout?e=legacy.optout%40example.com&t={OptOutLinks.Legacy("legacy.optout@example.com")}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client().GetAsync("/api/optout?e=x%40example.com&t=forged")).StatusCode);

        // a delivered order waiting for its rating, and an active designer for new-order alerts
        var (_, o1) = await app.Client().PostForm("/api/orders", OrderTests.Form(OrderTests.Fields("design", "rate.me@example.com")));
        var delivered = app.Clock.GetUtcNow().AddDays(-5);
        await app.Db(db => db.Orders.Where(o => o.Id == o1.Int("orderId")).ExecuteUpdateAsync(s => s.SetProperty(o => o.DeliveredAt, delivered).SetProperty(o => o.Status, OrderStatuses.Delivered).SetProperty(o => o.Designer, "d@example.com")));
        await app.Db(async db =>
        {
            db.Designers.Add(new Designer { Email = "alert.designer@example.com", Name = "A", Status = DesignerStatuses.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });

        var ran = await app.Send(new RunDueJobsCommand()); // Tuesday 10:00 Riyadh
        Assert.Equal(new[] { "daily:2026-10-13" }, ran);
        Assert.Empty(await app.Send(new RunDueJobsCommand())); // once per day

        var nudge = app.Email.To("nudge.me@example.com").Single();
        Assert.Equal("Your plan is ready for the next step | Oxira Design", nudge.Subject);
        Assert.Contains("/en/plan/?p=", nudge.Html);
        Assert.Empty(app.Email.To("too.new@example.com"));
        Assert.Empty(app.Email.To("too.old@example.com"));
        Assert.DoesNotContain(app.Email.To("ordered@example.com"), m => m.Subject.Contains("الخطوة الجاية"));
        Assert.Empty(app.Email.To("legacy.optout@example.com"));
        Assert.Single(app.Email.To("rate.me@example.com"), m => m.Subject.StartsWith("قيّم طلبك #"));
        var alert = app.Email.To("alert.designer@example.com").Single(m => m.Subject.Contains("New order available")); // the delivered one has a designer
        Assert.Equal(new[] { "info@oxira.sa" }, alert.To);

        // the follow-up's unsubscribe link stops the next ones
        var link = new Uri(Regex.Match(nudge.Html, "href=\"([^\"]*/api/optout\\?[^\"]+)\"").Groups[1].Value.Replace("&amp;", "&"));
        Assert.Equal(HttpStatusCode.OK, (await app.Client().GetAsync(link.PathAndQuery)).StatusCode);
        Assert.True(await app.Db(db => db.OptOuts.AnyAsync(x => x.Email == "nudge.me@example.com")));

        var before = app.Email.Sent.Count;
        Assert.Equal(0, await app.Send(new DailyGrowthCommand())); // everything was flagged
        Assert.Equal(before, app.Email.Sent.Count);
    }

    [Fact]
    public async Task Weekly_report_has_numbers_and_captions()
    {
        app.Ai.Script.Enqueue(FakeAnthropic.Text("{\"instagram_ar\": \"مخطط فيلا ٢٠ في ٣٠\", \"x_en\": \"A 20x30 villa plan\"}"));
        Assert.True(await app.Send(new WeeklyReportCommand()));
        var mail = app.Email.To("info@oxira.sa").Last(m => m.Subject.StartsWith("Oxira Design · الأسبوع بالأرقام"));
        Assert.Contains("مخطط فيلا ٢٠ في ٣٠", mail.Html);
        Assert.Contains("design.oxira.sa/plans/", mail.Html);
    }
}
