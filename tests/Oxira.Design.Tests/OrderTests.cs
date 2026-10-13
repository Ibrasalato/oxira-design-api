using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Oxira.Design.Domain;

namespace Oxira.Design.Tests;

public class OrderTests(TestApp app) : IClassFixture<TestApp>
{
    public static MultipartFormDataContent Form(Dictionary<string, string> fields, params (string Field, string Name, byte[] Data)[] files)
    {
        var f = new MultipartFormDataContent();
        foreach (var (k, v) in fields) f.Add(new StringContent(v), k);
        foreach (var (field, name, data) in files)
        {
            var c = new ByteArrayContent(data);
            c.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            f.Add(c, field, name);
        }
        return f;
    }

    public static Dictionary<string, string> Fields(string package, string email = "Client@Example.com", string lang = "ar") => new()
    {
        ["package"] = package, ["name"] = "أحمد", ["phone"] = "+966500000001", ["email"] = email, ["city"] = "الرياض", ["area"] = "120",
        ["style"] = "modern", ["notes"] = "شقة ثلاث غرف", ["lang"] = lang, ["page"] = "https://design.oxira.sa/", ["returnUrl"] = "https://design.oxira.sa/",
        ["summary"] = JsonSerializer.Serialize(new { totalArea = 118.5, rooms = new[] { new { name = "Living", type = "living", area = 30.2 } }, share = "abcdefghijkmnpqr" }),
    };

    [Fact]
    public async Task Quick_order_stores_files_emails_and_returns_a_pay_link_then_the_callback_marks_it_paid_once()
    {
        var dxf = Encoding.ASCII.GetBytes("0\nSECTION\n");
        var (status, body) = await app.Client().PostForm("/api/orders", Form(Fields("quick"),
            ("plan0", "plan.dxf", dxf), ("plan1", "../../evil.exe", [1, 2, 3]), ("preview", "view.jpg", [0xFF, 0xD8, 0xFF, 0xD9])));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.Bool("success"));
        var id = body.Int("orderId");
        Assert.StartsWith("https://checkout.moyasar.com/", body.Str("payUrl"));

        var req = app.Payments.Requests.Last();
        Assert.Equal(28635, req.Amount); // 249 + 15% VAT
        Assert.Equal($"https://design.oxira.sa/?paid={id}#order", req.SuccessUrl);
        Assert.Equal($"https://design.oxira.sa/?cancel={id}#order", req.BackUrl);
        Assert.Equal("design", req.Metadata["kind"]);

        var order = await app.Db(db => db.Orders.Include(o => o.Files).SingleAsync(o => o.Id == id));
        Assert.Equal("client@example.com", order.Email);
        Assert.Equal(OrderStatuses.Unpaid, order.Status);
        Assert.Equal(286.35m, order.AmountDue);
        Assert.Equal(2, order.Files.Count); // the .exe was dropped
        Assert.All(order.Files, f => Assert.True(File.Exists(Path.Combine(app.Storage, f.Path))));
        Assert.Contains("Living (living) 30.2", order.Rooms);

        var team = app.Email.To("info@oxira.sa").Single(m => m.Subject.Contains($"#{id} "));
        Assert.Equal(2, team.Attachments!.Count);
        Assert.Equal("client@example.com", team.ReplyTo);
        Assert.Contains(app.Email.To("client@example.com"), m => m.Subject.Contains($"استلمنا طلبك #{id}"));

        // the page waits for the payment over SignalR
        await using var hub = await app.Hub();
        var paid = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<JsonElement>("paid", e => paid.TrySetResult(e));
        await hub.InvokeAsync("JoinPayment", id);

        var (_, before) = await app.Client().GetJson($"/api/orders/{id}/payment");
        Assert.False(before.Bool("paid"));

        var inv = app.Payments.Last("design");
        // an unpaid invoice changes nothing
        await app.Client().PostJson("/api/payments/moyasar/callback", new { id = inv, status = "paid" });
        Assert.Equal(OrderStatuses.Unpaid, (await app.Db(db => db.Orders.SingleAsync(o => o.Id == id))).Status);

        app.Payments.Pay(inv);
        (status, _) = await app.Client().PostJson("/api/payments/moyasar/callback", new { type = "payment_paid", data = new { id = "pay_1", invoice_id = inv } });
        Assert.Equal(HttpStatusCode.OK, status);
        await app.Client().PostJson("/api/payments/moyasar/callback", new { id = inv }); // a repeat is ignored

        Assert.Equal(id, (await paid.Task.WaitAsync(TimeSpan.FromSeconds(10))).Int("orderId"));
        var (_, after) = await app.Client().GetJson($"/api/orders/{id}/payment");
        Assert.True(after.Bool("paid"));
        Assert.Equal(OrderStatuses.Paid, (await app.Db(db => db.Orders.SingleAsync(o => o.Id == id))).Status);
        Assert.Single(app.Email.To("info@oxira.sa"), m => m.Subject.Contains($"تم الدفع ✅ طلب تصميم #{id} "));
        Assert.Single(app.Email.To("client@example.com"), m => m.Subject.Contains($"تم استلام الدفع لطلبك #{id} "));
    }

    [Fact]
    public async Task Design_order_is_priced_on_the_server_and_has_no_pay_link()
    {
        var f = Fields("design", "lena@example.de", "de");
        f["area"] = "50";
        var (status, body) = await app.Client().PostForm("/api/orders", Form(f));
        Assert.Equal(HttpStatusCode.OK, status);
        var id = body.Int("orderId");
        Assert.False(body.TryGetProperty("payUrl", out _));
        var o = await app.Db(db => db.Orders.SingleAsync(x => x.Id == id));
        Assert.Equal(OrderStatuses.New, o.Status);
        Assert.Contains("2500 ريال", o.PriceNote); // 50 m² × 35 is below the minimum
        Assert.Contains(app.Email.To("lena@example.de"), m => m.Subject == $"Ihre Bestellung #{id} ist eingegangen | Oxira Design");
    }

    [Fact]
    public async Task Invalid_orders_honeypot_and_return_url()
    {
        var f = Fields("quick");
        f.Remove("name");
        var (status, body) = await app.Client().PostForm("/api/orders", Form(f));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.False(body.Bool("success"));
        Assert.Equal("invalid", body.Str("reason"));

        f = Fields("quick");
        f["area"] = "5";
        (status, _) = await app.Client().PostForm("/api/orders", Form(f));
        Assert.Equal(HttpStatusCode.BadRequest, status);

        var before = app.Email.Sent.Count;
        f = Fields("quick");
        f["company_website"] = "http://spam";
        (status, body) = await app.Client().PostForm("/api/orders", Form(f));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, body.Int("orderId"));
        Assert.Equal(before, app.Email.Sent.Count);

        f = Fields("quick");
        f["returnUrl"] = "https://evil.example/";
        (_, body) = await app.Client().PostForm("/api/orders", Form(f));
        Assert.StartsWith("https://design.oxira.sa/?paid=", app.Payments.Requests.Last().SuccessUrl);

        (status, body) = await app.Client().PostForm("/api/orders", Form(Fields("plan"), ("plan0", "big.pdf", new byte[17 * 1024 * 1024])));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Equal("too_big", body.Str("reason"));

        (status, body) = await app.Client().GetJson("/api/orders/999999/payment");
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not_found", body.Str("reason"));
    }

    [Fact]
    public async Task Order_is_saved_even_when_moyasar_is_down()
    {
        app.Payments.Fail = true;
        try
        {
            var (status, body) = await app.Client().PostForm("/api/orders", Form(Fields("quick")));
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(body.Int("orderId") > 0);
            Assert.False(body.TryGetProperty("payUrl", out _));
        }
        finally { app.Payments.Fail = false; }
    }
}

public class ChatTests(TestApp app) : IClassFixture<TestApp>
{
    [Fact]
    public async Task Chat_saves_a_lead_with_the_tool_and_emails_the_team()
    {
        app.Ai.Script.Enqueue(FakeAnthropic.Tool("create_lead", new JsonObject
        {
            ["contact_name"] = "Sara", ["phone"] = "966511111111", ["email"] = "sara@example.com", ["package"] = "design", ["area"] = "180", ["needs"] = "تصميم فيلا",
        }));
        app.Ai.Script.Enqueue(FakeAnthropic.Text("تم تسجيل طلبك رقم 1، وفريق أوكسيرا سيتواصل معك."));

        // the chat widget posts a URL-encoded form
        var (status, body) = await app.Client().PostForm("/api/chat", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["sessionId"] = "sess-abc-123", ["message"] = "نعم أكد الطلب", ["lang"] = "ar", ["page"] = "https://design.oxira.sa/studio/", ["context"] = "Studio plan: total {120.7} m2",
        }));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.Bool("success"));
        Assert.Contains("تم تسجيل طلبك", body.Str("reply"));

        var lead = await app.Db(db => db.Leads.SingleAsync(l => l.ContactName == "Sara"));
        Assert.Equal("website-chat", lead.Channel);
        Assert.Equal("Studio plan: total 120.7 m2", lead.Context);
        Assert.Contains(app.Email.To("info@oxira.sa"), m => m.Subject.Contains($"رقم {lead.Id}: Sara") && m.ReplyTo == "sara@example.com");

        var system = app.Ai.Calls[^2].System;
        Assert.Contains("Studio plan: total 120.7 m2", system);

        // memory: the next call carries the earlier turns
        (_, body) = await app.Client().PostText("/api/chat", new { sessionId = "sess-abc-123", message = "شكراً", lang = "ar" });
        Assert.True(body.Bool("success"));
        var msgs = app.Ai.Calls[^1].Messages;
        Assert.Equal(3, msgs.Count);
        Assert.Equal("نعم أكد الطلب", (string?)msgs[0]!["content"]);

        var (_, history) = await app.Client().GetJson("/api/chat/sess-abc-123");
        Assert.Equal(4, history.GetArrayLength());
    }

    [Fact]
    public async Task Chat_failure_and_missing_message()
    {
        app.Ai.Fail = true;
        try
        {
            var (status, body) = await app.Client().PostJson("/api/chat", new { sessionId = "sess-fail-1", message = "مرحبا" });
            Assert.Equal((HttpStatusCode)424, status);
            Assert.False(body.Bool("success"));
            Assert.Contains("واتساب", body.Str("reply"));
        }
        finally { app.Ai.Fail = false; }

        var (s2, b2) = await app.Client().PostJson("/api/chat", new { sessionId = "sess-x", message = "" });
        Assert.Equal(HttpStatusCode.BadRequest, s2);
        Assert.Equal("missing", b2.Str("reason"));
    }
}

public class PlatformTests(TestApp app) : IClassFixture<TestApp>
{
    [Fact]
    public async Task Health_swagger_and_cors()
    {
        Assert.Equal(HttpStatusCode.OK, (await app.Client().GetAsync("/health")).StatusCode);
        var doc = await app.Client().GetStringAsync("/openapi/v1.json");
        Assert.Contains("Oxira Design API", doc);
        Assert.Contains("/api/orders", doc);

        var req = new HttpRequestMessage(HttpMethod.Options, "/api/chat");
        req.Headers.Add("Origin", "https://design.oxira.sa");
        req.Headers.Add("Access-Control-Request-Method", "POST");
        req.Headers.Add("Access-Control-Request-Headers", "content-type,authorization");
        var res = await app.Client().SendAsync(req);
        Assert.Equal("https://design.oxira.sa", res.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Hub_order_groups_need_an_account()
    {
        await using var anon = await app.Hub();
        Assert.False(await anon.InvokeAsync<bool>("JoinOrder", 1));
        await anon.InvokeAsync("JoinWallet", "abcdefghijklmnop1234");
    }
}
