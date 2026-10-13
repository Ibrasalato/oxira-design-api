using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Oxira.Design.Domain;

namespace Oxira.Design.Tests;

public class AccountTests(TestApp app) : IClassFixture<TestApp>
{
    private async Task<int> OrderAsync(string email, string package = "plan")
    {
        var (_, body) = await app.Client().PostForm("/api/orders", OrderTests.Form(OrderTests.Fields(package, email)));
        return body.Int("orderId");
    }

    private async Task DesignerAsync(string email, string status)
    {
        await app.Db(async db =>
        {
            db.Designers.Add(new Designer { Email = email, Name = "Omar Designer", Skills = "Facades", Countries = "EG", Status = status, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Account_needs_an_oxira_token()
    {
        var (status, body) = await app.Client().GetJson("/api/account");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("session", body.Str("reason"));

        // a token signed with another key is refused
        var bad = app.Client();
        bad.DefaultRequestHeaders.Authorization = new("Bearer", "eyJhbGciOiJIUzI1NiJ9.eyJlbWFpbCI6ImFAYi5jIn0.x");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bad.GetJson("/api/account")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client().PostJson("/api/projects", new { title = "x", data = "{}" })).Status);
    }

    [Fact]
    public async Task Projects_save_share_publish_and_delete()
    {
        var c = app.Client("Owner@Example.com");
        var (status, saved) = await c.PostJson("/api/projects", new { title = "<b>Villa</b> 20x30", kind = "plan", data = "{\"v\":1,\"rooms\":[]}", lang = "en" });
        Assert.Equal(HttpStatusCode.OK, status);
        var pid = saved.Str("pid");
        var share = saved.Str("share");
        Assert.Equal(14, pid.Length);
        Assert.Equal(16, share.Length);

        // saving again with the pid updates the same plan; data may also be sent as an object
        (_, saved) = await c.PostJson("/api/projects", new { pid, title = "Villa v2", data = new { v = 2, rooms = Array.Empty<object>() } });
        Assert.Equal(pid, saved.Str("pid"));
        var (_, proj) = await c.GetJson($"/api/projects/{pid}");
        Assert.Equal("Villa v2", proj.GetProperty("project").Str("title"));
        Assert.Contains("\"v\":2", proj.GetProperty("project").Str("data"));

        // somebody else cannot open it, but the share link works for everyone
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client("other@example.com").GetJson($"/api/projects/{pid}")).Status);
        var (_, shared) = await app.Client().GetJson($"/api/share/{share}");
        Assert.True(shared.Bool("success"));
        Assert.Equal("Villa v2", shared.Str("title"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client().GetJson("/api/share/aaaaaaaaaaaaaaaa")).Status);

        var (_, pub) = await c.PostJson($"/api/projects/{pid}/publish", new { @public = true });
        Assert.True(pub.Bool("public"));
        var (_, gallery) = await app.Client().GetJson("/api/gallery");
        var item = gallery.GetProperty("items").EnumerateArray().Single(i => i.Str("share") == share);
        Assert.False(item.TryGetProperty("owner", out _));
        Assert.Contains(app.Email.To("info@oxira.sa"), m => m.Subject.StartsWith("مخطط جديد في المعرض: Villa v2"));

        var (_, view) = await c.GetJson("/api/account");
        Assert.Equal("owner@example.com", view.Str("email"));
        Assert.Equal("", view.Str("designer"));
        Assert.True(view.GetProperty("projects")[0].Bool("public"));
        Assert.False(view.TryGetProperty("open", out _));

        (status, _) = await c.Send(HttpMethod.Delete, $"/api/projects/{pid}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetJson($"/api/projects/{pid}")).Status);

        (status, var big) = await c.PostJson("/api/projects", new { title = "big", data = new string('x', 400_001) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Equal("too_big", big.Str("reason"));
    }

    [Fact]
    public async Task Designer_flow_claim_comment_deliver_and_rate_with_live_events()
    {
        const string client = "client.flow@example.com", designer = "designer.flow@example.com";
        var orderId = await OrderAsync(client);
        var c = app.Client(client);
        var d = app.Client(designer);

        // a client account cannot claim; an applicant is pending until approved
        Assert.Equal(HttpStatusCode.Conflict, (await d.PostJson($"/api/orders/{orderId}/claim", new { })).Status);
        var (status, _) = await d.PostJson("/api/designers/apply", new { name = "Sara Designer", skills = "Interior, 3ds Max", countries = "SA", note = "10 years" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(HttpStatusCode.Conflict, (await d.PostJson("/api/designers/apply", new { name = "Sara", skills = "x" })).Status);
        var (_, pending) = await d.GetJson("/api/account");
        Assert.Equal("pending", pending.Str("designer"));
        await app.Db(db => db.Designers.Where(x => x.Email == designer).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DesignerStatuses.Active)));

        var (_, dview) = await d.GetJson("/api/account");
        var open = dview.GetProperty("open").EnumerateArray().Single(o => o.Int("id") == orderId);
        Assert.Equal("أحمد", open.Str("client"));
        Assert.Equal("abcdefghijkmnpqr", open.Str("share"));

        // the client's page listens for this order
        await using var hub = await app.Hub(client);
        var events = new List<JsonElement>();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<JsonElement>("order", e => { lock (events) events.Add(e); if (e.Str("kind") == "delivered") delivered.TrySetResult(); });
        Assert.True(await hub.InvokeAsync<bool>("JoinOrder", orderId));

        (status, _) = await d.PostJson($"/api/orders/{orderId}/claim", new { });
        Assert.Equal(HttpStatusCode.OK, status);
        var (s2, b2) = await app.Client("second.designer@example.com", "staff").PostJson($"/api/orders/{orderId}/claim", new { });
        Assert.Equal(HttpStatusCode.Conflict, s2);
        Assert.Equal("not_available", b2.Str("reason"));

        (status, _) = await c.PostJson($"/api/orders/{orderId}/comments", new { text = "متى التسليم؟" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains(app.Email.To(designer), m => m.Html.Contains("متى التسليم؟"));
        await d.PostJson($"/api/orders/{orderId}/comments", new { text = "Tomorrow" });
        Assert.Contains(app.Email.To(client), m => m.Html.Contains("Tomorrow"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client("stranger@example.com").PostJson($"/api/orders/{orderId}/comments", new { text = "hi" })).Status);

        // rating before delivery is refused
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostJson($"/api/orders/{orderId}/rating", new { rating = 5 })).Status);

        // only the order's designer delivers
        var files = OrderTests.Form(new() { ["note"] = "Here are the drawings" }, ("file0", "plans.pdf", [1, 2, 3]), ("file1", "model.max", [4, 5]), ("file2", "virus.exe", [6]));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client("stranger@example.com").PostForm($"/api/orders/{orderId}/delivery", files)).Status);
        files = OrderTests.Form(new() { ["note"] = "Here are the drawings" }, ("file0", "plans.pdf", [1, 2, 3]), ("file1", "model.max", [4, 5]), ("file2", "virus.exe", [6]));
        (status, _) = await d.PostForm($"/api/orders/{orderId}/delivery", files);
        Assert.Equal(HttpStatusCode.OK, status);
        var mail = app.Email.To(client).Single(m => m.Subject.StartsWith($"ملفات طلبك #{orderId} "));
        Assert.Equal(2, mail.Attachments!.Count);
        Assert.Equal(new[] { "info@oxira.sa" }, mail.Bcc);
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lock (events) Assert.Contains(events, e => e.Str("kind") == "comment");

        var (s5, _) = await c.PostJson($"/api/orders/{orderId}/rating", new { rating = 5, review = "Excellent work" });
        Assert.Equal(HttpStatusCode.OK, s5);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostJson($"/api/orders/{orderId}/rating", new { rating = 9 })).Status);

        var (_, cview) = await c.GetJson("/api/account");
        var mine = cview.GetProperty("orders").EnumerateArray().Single(o => o.Int("id") == orderId);
        Assert.Equal(OrderStatuses.Delivered, mine.Str("status"));
        Assert.True(mine.Bool("has_designer"));
        Assert.Equal(5, mine.Int("rating"));
        Assert.True(mine.TryGetProperty("delivered_at", out _));
        Assert.False(mine.TryGetProperty("notes", out _)); // designer-only fields stay out of the client view
        var thread = mine.GetProperty("comments").EnumerateArray().ToList();
        Assert.Equal(3, thread.Count);
        Assert.True(thread[0].Bool("mine"));
        Assert.Equal("designer", thread[2].Str("role"));

        var (_, dir) = await app.Client().GetJson("/api/designers");
        var card = dir.GetProperty("items").EnumerateArray().Single(x => x.Str("name") == "Sara Designer");
        Assert.Equal(5, card.GetProperty("rating").GetDouble());
        Assert.Equal("Excellent work", card.GetProperty("quotes")[0].GetString());
        Assert.DoesNotContain("@", dir.GetRawText());

        var stored = await app.Db(db => db.OrderFiles.Where(f => f.OrderId == orderId && f.Role == "delivery").CountAsync());
        Assert.Equal(2, stored);
    }

    [Fact]
    public async Task Staff_are_design_admins()
    {
        var orderId = await OrderAsync("someone.else@example.com");
        await DesignerAsync("owner.designer@example.com", DesignerStatuses.Active);
        await app.Client("owner.designer@example.com").PostJson($"/api/orders/{orderId}/claim", new { });
        var (_, view) = await app.Client("boss@oxira.sa", "admin").GetJson("/api/account");
        Assert.Equal("admin", view.Str("designer"));
        Assert.Contains(view.GetProperty("mine").EnumerateArray(), o => o.Int("id") == orderId && o.Str("designer") == "owner.designer@example.com");
    }

    [Fact]
    public async Task Waitlist()
    {
        var (status, _) = await app.Client().PostText("/api/waitlist", new { email = "Pro@Example.com", plan = "pro", role = "architect", lang = "en", country = "ae" });
        Assert.Equal(HttpStatusCode.OK, status);
        await app.Client().PostText("/api/waitlist", new { email = "pro@example.com", role = "developer" });
        Assert.Equal("developer", (await app.Db(db => db.Waitlist.SingleAsync(w => w.Email == "pro@example.com"))).Role);
        var (s2, b2) = await app.Client().PostText("/api/waitlist", new { email = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, s2);
        Assert.Equal("email", b2.Str("reason"));
    }
}
