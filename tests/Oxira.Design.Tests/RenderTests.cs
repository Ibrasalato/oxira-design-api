using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;

namespace Oxira.Design.Tests;

public class RenderTests(TestApp app) : IClassFixture<TestApp>
{
    private static object Studio(string sid, string? wallet = null, string? @ref = null, string source = "snapshot") =>
        new { sessionId = sid, image = Http.Jpeg, style = "najdi", view = "walk", room = "living", lang = "ar", source, @ref, wallet };

    private async Task<string> BuyAsync(string wallet, string pack = "p10")
    {
        var (status, body) = await app.Client().PostText("/api/wallets/purchases", new { wallet, pack, email = "buyer@example.com", lang = "en", returnUrl = "https://design.oxira.sa/en/redesign/?x=1#top" });
        Assert.Equal(HttpStatusCode.OK, status);
        var inv = app.Payments.Last("redesign");
        app.Payments.Pay(inv);
        await app.Client().PostJson("/api/payments/moyasar/callback", new { id = inv });
        return body.Int("purchaseId").ToString();
    }

    [Fact]
    public async Task Studio_renders_are_free_three_times_then_need_credits()
    {
        var c = app.Client();
        for (var n = 2; n >= 0; n--)
        {
            var (status, body) = await c.PostText("/api/renders", Studio("studio-sess-1", @ref: Http.Jpeg, source: "traced"));
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(body.Bool("success"));
            Assert.StartsWith("data:image/jpeg;base64,", body.Str("image"));
            Assert.Equal(n, body.Int("remaining"));
        }
        var last = app.Images.Calls.Last();
        Assert.Equal(2, last.Images.Count); // the style reference goes along
        Assert.Equal("high", last.Quality);
        Assert.Contains("Najdi", last.Prompt);
        Assert.Contains("path-traced", last.Prompt);

        var (s4, b4) = await c.PostText("/api/renders", Studio("studio-sess-1"));
        Assert.Equal((HttpStatusCode)429, s4);
        Assert.Equal("limit", b4.Str("reason"));
        Assert.Equal(0, b4.Int("remaining"));

        // a wallet without credits
        var (s5, b5) = await c.PostText("/api/renders", Studio("studio-sess-1", "walletwithoutcredit1"));
        Assert.Equal(HttpStatusCode.PaymentRequired, s5);
        Assert.Equal("credits", b5.Str("reason"));

        // with credits: one is spent, and given back when the render fails
        const string wallet = "studiowallet0000000001";
        await BuyAsync(wallet);
        var (s6, b6) = await c.PostText("/api/renders", Studio("studio-sess-1", wallet));
        Assert.Equal(HttpStatusCode.OK, s6);
        Assert.Equal(9, b6.Int("credits"));
        app.Images.Fail = true;
        try
        {
            var (s7, b7) = await c.PostText("/api/renders", Studio("studio-sess-1", wallet));
            Assert.Equal((HttpStatusCode)424, s7);
            Assert.Equal("failed", b7.Str("reason"));
            Assert.Equal(9, b7.Int("credits"));
        }
        finally { app.Images.Fail = false; }

        var (s8, b8) = await c.PostText("/api/renders", new { sessionId = "studio-sess-2", image = "data:text/html;base64,AAAA" });
        Assert.Equal(HttpStatusCode.BadRequest, s8);
        Assert.Equal("invalid", b8.Str("reason"));
    }

    [Fact]
    public async Task Room_redesign_free_then_pack_purchase_credits_the_wallet_live()
    {
        const string wallet = "roomwallet000000000001";
        var c = app.Client();
        var (s0, w0) = await c.GetJson($"/api/wallets/{wallet}?sessionId=room-sess-1");
        Assert.Equal(HttpStatusCode.OK, s0);
        Assert.Equal(0, w0.Int("credits"));
        Assert.Equal(1, w0.Int("free_left"));

        var (s1, b1) = await c.PostJson("/api/redesign/renders", new { sessionId = "room-sess-1", wallet, style = "luxury", room = "majlis", lang = "ar", image = Http.Jpeg });
        Assert.Equal(HttpStatusCode.OK, s1);
        Assert.True(b1.Bool("success"));
        Assert.Equal(0, b1.Int("free_left"));
        var free = app.Images.Calls.Last();
        Assert.Equal("gpt-image-1-mini", free.Model);
        Assert.Equal("low", free.InputFidelity);
        Assert.Contains("majlis", free.Prompt);

        var (s2, b2) = await c.PostJson("/api/redesign/renders", new { sessionId = "room-sess-1", wallet, image = Http.Jpeg });
        Assert.Equal(HttpStatusCode.PaymentRequired, s2);
        Assert.Equal("credits", b2.Str("reason"));

        // buy p30; the page hears about the credits over SignalR
        await using var hub = await app.Hub();
        var live = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<JsonElement>("wallet", e => { if (e.GetProperty("credits").GetInt32() == 30) live.TrySetResult(e); });
        await hub.InvokeAsync("JoinWallet", wallet);

        var (s3, b3) = await c.PostText("/api/wallets/purchases", new { wallet, pack = "p30", email = "buyer@example.com", lang = "en", returnUrl = "https://design.oxira.sa/en/redesign/?w=old" });
        Assert.Equal(HttpStatusCode.OK, s3);
        var purchaseId = b3.Int("purchaseId");
        var req = app.Payments.Requests.Last();
        Assert.Equal(6900, req.Amount);
        Assert.Equal($"https://design.oxira.sa/en/redesign/?bought={purchaseId}", req.SuccessUrl);
        var inv = app.Payments.Last("redesign");
        app.Payments.Pay(inv);
        await c.PostJson("/api/payments/moyasar/callback", new { id = inv });
        await c.PostJson("/api/payments/moyasar/callback", new { id = inv }); // credited once
        Assert.Equal(wallet, (await live.Task.WaitAsync(TimeSpan.FromSeconds(10))).GetProperty("wallet").GetString());

        var (_, w1) = await c.GetJson($"/api/wallets/{wallet}?sessionId=room-sess-1");
        Assert.Equal(30, w1.Int("credits"));
        var receipt = app.Email.To("buyer@example.com").Single(m => m.Subject.Contains("30 room renders"));
        Assert.Contains($"/en/redesign/?w={wallet}", receipt.Html);

        var (s4, b4) = await c.PostJson("/api/redesign/renders", new { sessionId = "room-sess-1", wallet, style = "modern", room = "kitchen", image = Http.Jpeg });
        Assert.Equal(HttpStatusCode.OK, s4);
        Assert.Equal(29, b4.Int("credits"));
        Assert.Equal("gpt-image-1", app.Images.Calls.Last().Model);
        Assert.Equal("high", app.Images.Calls.Last().InputFidelity);

        app.Images.Fail = true;
        try
        {
            var (_, b5) = await c.PostJson("/api/redesign/renders", new { sessionId = "room-sess-1", wallet, image = Http.Jpeg });
            Assert.Equal("failed", b5.Str("reason"));
            Assert.Equal(29, b5.Int("credits"));
        }
        finally { app.Images.Fail = false; }

        var (s6, b6) = await c.PostText("/api/wallets/purchases", new { wallet = "short", pack = "p10" });
        Assert.Equal(HttpStatusCode.BadRequest, s6);
        Assert.Equal("invalid", b6.Str("reason"));

        app.Payments.Fail = true;
        try
        {
            var (s7, b7) = await c.PostText("/api/wallets/purchases", new { wallet, pack = "p10" });
            Assert.Equal((HttpStatusCode)424, s7);
            Assert.Equal("payments", b7.Str("reason"));
        }
        finally { app.Payments.Fail = false; }
        Assert.Equal(1, await app.Db(db => db.Purchases.CountAsync(p => p.WalletId == wallet && p.Status == "paid")));
    }

    [Fact]
    public async Task Plan_ai_brief_and_image()
    {
        app.Ai.Script.Enqueue(FakeAnthropic.Text("Sure: {\"patch\": {\"type\": \"villa\", \"land\": {\"w\": 20, \"d\": 30}}, \"reply\": \"تم ضبط فيلا على أرض ٢٠×٣٠\"}"));
        var (status, body) = await app.Client().PostJson("/api/plan-ai", new { kind = "brief", text = "فيلا دورين ٥ غرف نوم على أرض ٢٠ في ٣٠", lang = "ar", region = "SA", brief = new { type = "duplex" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("villa", body.GetProperty("patch").GetProperty("type").GetString());
        Assert.Contains("فيلا", body.Str("reply"));
        Assert.Contains("Country of the client: SA", app.Ai.Calls.Last().System);
        Assert.Contains("\"type\":\"duplex\"", (string?)app.Ai.Calls.Last().Messages[0]!["content"]);

        app.Ai.Script.Enqueue(FakeAnthropic.Text("{\"width\": 12, \"depth\": 15.5, \"rooms\": [{\"name\": \"صالة\", \"type\": \"living\", \"x\": 0, \"y\": 0, \"w\": 6, \"h\": 5}, {\"name\": \"tiny\", \"type\": \"wc\", \"x\": 0, \"y\": 0, \"w\": 0.1, \"h\": 1}], \"notes\": \"ok\"}"));
        (status, body) = await app.Client().PostText("/api/plan-ai", new { kind = "image", image = Http.Jpeg, knownWidth = 12, lang = "ar" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(12, body.GetProperty("width").GetDouble());
        Assert.Single(body.GetProperty("rooms").EnumerateArray());
        Assert.Equal("image", (string?)app.Ai.Calls.Last().Messages[0]!["content"]![0]!["type"]);

        app.Ai.Script.Enqueue(FakeAnthropic.Text("{\"width\": 1, \"depth\": 1, \"rooms\": []}"));
        (status, body) = await app.Client().PostText("/api/plan-ai", new { kind = "image", image = Http.Jpeg });
        Assert.Equal((HttpStatusCode)422, status);
        Assert.Equal("unreadable", body.Str("reason"));

        app.Ai.Script.Enqueue(FakeAnthropic.Text("I cannot help"));
        (_, body) = await app.Client().PostText("/api/plan-ai", new { kind = "brief", text = "something long enough" });
        Assert.Equal("parse", body.Str("reason"));

        (status, body) = await app.Client().PostText("/api/plan-ai", new { kind = "brief", text = "hi" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("empty", body.Str("reason"));
        (_, body) = await app.Client().PostText("/api/plan-ai", new { kind = "image", image = "nope" });
        Assert.Equal("image", body.Str("reason"));

        // 8 images per IP per day (2 counted above: a bad image is refused before counting)
        for (var n = 0; n < 6; n++)
        {
            app.Ai.Script.Enqueue(FakeAnthropic.Text("{\"width\": 10, \"depth\": 10, \"rooms\": [{\"name\": \"a\", \"type\": \"hall\", \"x\": 0, \"y\": 0, \"w\": 10, \"h\": 10}]}"));
            (status, _) = await app.Client().PostText("/api/plan-ai", new { kind = "image", image = Http.Jpeg });
            Assert.Equal(HttpStatusCode.OK, status);
        }
        (status, body) = await app.Client().PostText("/api/plan-ai", new { kind = "image", image = Http.Jpeg });
        Assert.Equal((HttpStatusCode)429, status);
        Assert.Equal("limit", body.Str("reason"));
    }
}
