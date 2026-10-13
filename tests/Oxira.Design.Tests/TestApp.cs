using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Oxira.Design.Api.Infrastructure;
using Oxira.Design.Application.Common;
using Oxira.Design.Infrastructure.Data;

namespace Oxira.Design.Tests;

/// <summary>The real API on a real SQL Server (OXIRA_DESIGN_TEST_DB), with fakes for email, AI, images and Moyasar.</summary>
public class TestApp : WebApplicationFactory<Program>
{
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 13, 7, 0, 0, TimeSpan.Zero)); // Tue 10:00 Riyadh
    public FakeEmail Email { get; } = new();
    public FakeAnthropic Ai { get; } = new();
    public FakeImages Images { get; } = new();
    public FakePayments Payments { get; } = new();
    public string Storage { get; } = Path.Combine(Path.GetTempPath(), "oxira-design-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _db = "OxiraDesignTest_" + Guid.NewGuid().ToString("N")[..8];

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        var server = Environment.GetEnvironmentVariable("OXIRA_DESIGN_TEST_DB") ?? "Server=localhost,1433;User Id=sa;Password=Oxira_dev_2026!;TrustServerCertificate=True";
        b.UseEnvironment("Development");
        b.UseSetting("ConnectionStrings:Design", $"{server};Database={_db}");
        b.UseSetting("Storage:Root", Storage);
        b.UseSetting("Payments:Simulate", "false");
        // on, as in production: a CF-Connecting-IP header from a non-Cloudflare address must be ignored
        b.UseSetting("Proxy:TrustCloudflare", "true");
        b.UseSetting("Payments:SecretKey", "sk_test_fake");
        foreach (var k in new[] { "FormsPerMinute", "ChatPerMinute", "RenderPerMinute", "AiPerMinute", "ReadsPerMinute" })
            b.UseSetting($"RateLimit:{k}", "100000");
        b.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Clock);
            s.RemoveAll<IEmailSender>();
            s.AddSingleton<IEmailSender>(Email);
            s.RemoveAll<IAnthropic>();
            s.AddSingleton<IAnthropic>(Ai);
            s.RemoveAll<IImageEditor>();
            s.AddSingleton<IImageEditor>(Images);
            s.RemoveAll<IPayments>();
            s.AddSingleton<IPayments>(Payments);
            var jobs = s.FirstOrDefault(d => d.ImplementationType == typeof(JobsHost));
            if (jobs != null) s.Remove(jobs);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await using var scope = Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DesignDbContext>().Database.EnsureDeletedAsync();
            if (Directory.Exists(Storage)) Directory.Delete(Storage, true);
        }
        catch { /* best effort */ }
        await base.DisposeAsync();
    }

    /// <summary>a client signed in as this Oxira account (role customer unless given)</summary>
    public HttpClient Client(string? email = null, string role = "customer")
    {
        var c = CreateClient();
        if (email != null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(email, role));
        return c;
    }

    public string Token(string email, string role = "customer") =>
        DevTokens.Issue(Services.GetRequiredService<IOptions<JwtOptions>>().Value, email, role, TimeSpan.FromHours(1));

    public async Task<T> Db<T>(Func<DesignDbContext, Task<T>> f)
    {
        await using var scope = Services.CreateAsyncScope();
        return await f(scope.ServiceProvider.GetRequiredService<DesignDbContext>());
    }

    public async Task<T> Send<T>(MediatR.IRequest<T> request)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MediatR.ISender>().Send(request);
    }

    public async Task<HubConnection> Hub(string? email = null)
    {
        var hub = new HubConnectionBuilder().WithUrl(new Uri(Server.BaseAddress, "hubs/design"), o =>
        {
            o.HttpMessageHandlerFactory = _ => Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
            if (email != null) o.AccessTokenProvider = () => Task.FromResult<string?>(Token(email));
        }).Build();
        await hub.StartAsync();
        return hub;
    }
}

public static class Http
{
    public static async Task<(HttpStatusCode Status, JsonElement Body)> PostJson(this HttpClient c, string url, object body)
    {
        var res = await c.PostAsJsonAsync(url, body);
        return (res.StatusCode, await Read(res));
    }

    /// <summary>JSON sent as text/plain, the way several pages avoid a CORS preflight</summary>
    public static async Task<(HttpStatusCode Status, JsonElement Body)> PostText(this HttpClient c, string url, object body)
    {
        var res = await c.PostAsync(url, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "text/plain"));
        return (res.StatusCode, await Read(res));
    }

    public static async Task<(HttpStatusCode Status, JsonElement Body)> PostForm(this HttpClient c, string url, HttpContent form)
    {
        var res = await c.PostAsync(url, form);
        return (res.StatusCode, await Read(res));
    }

    public static async Task<(HttpStatusCode Status, JsonElement Body)> GetJson(this HttpClient c, string url)
    {
        var res = await c.GetAsync(url);
        return (res.StatusCode, await Read(res));
    }

    public static async Task<(HttpStatusCode Status, JsonElement Body)> Send(this HttpClient c, HttpMethod method, string url, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (body != null) req.Content = JsonContent.Create(body);
        var res = await c.SendAsync(req);
        return (res.StatusCode, await Read(res));
    }

    public static async Task<JsonElement> Read(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        if ((int)res.StatusCode >= 500) throw new HttpRequestException($"{(int)res.StatusCode} {res.RequestMessage?.RequestUri}: {text}");
        return text.Length > 0 && (text[0] == '{' || text[0] == '[') ? JsonDocument.Parse(text).RootElement.Clone() : default;
    }

    public static string Str(this JsonElement e, string name) => e.GetProperty(name).GetString() ?? "";
    public static int Int(this JsonElement e, string name) => e.GetProperty(name).GetInt32();
    public static bool Bool(this JsonElement e, string name) => e.GetProperty(name).GetBoolean();

    /// <summary>a tiny JPEG data URL</summary>
    public const string Jpeg = "data:image/jpeg;base64,/9j/2Q==";
}

public class FakeEmail : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();
    public void Enqueue(EmailMessage message) => Sent.Enqueue(message);
    public List<EmailMessage> To(string address) => Sent.Where(m => m.To.Contains(address) || (m.Bcc?.Contains(address) ?? false)).ToList();
}

/// <summary>Scripted Claude: each call returns the next queued response (or a plain answer).</summary>
public class FakeAnthropic : IAnthropic
{
    public ConcurrentQueue<AiResponse> Script { get; } = new();
    public List<AiRequest> Calls { get; } = [];
    public bool Fail { get; set; }

    public Task<AiResponse> CreateMessageAsync(AiRequest request, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add(request with { Messages = (JsonArray)request.Messages.DeepClone() });
        if (Fail) throw new HttpRequestException("Anthropic down");
        return Task.FromResult(Script.TryDequeue(out var r) ? r : Text("أهلاً بيك، كيف أقدر أساعدك؟"));
    }

    public static AiResponse Text(string s) => new([new JsonObject { ["type"] = "text", ["text"] = s }], "end_turn");
    public static AiResponse Tool(string name, JsonObject input) =>
        new([new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_1", ["name"] = name, ["input"] = input }], "tool_use");
}

public class FakeImages : IImageEditor
{
    public bool Fail { get; set; }
    public ConcurrentQueue<ImageEdit> Calls { get; } = new();
    public Task<byte[]> EditAsync(ImageEdit request, CancellationToken ct = default)
    {
        Calls.Enqueue(request);
        if (Fail) throw new HttpRequestException("provider down");
        return Task.FromResult(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
    }
}

/// <summary>Moyasar stand-in: invoices are kept in memory and can be paid by the test.</summary>
public class FakePayments : IPayments
{
    public ConcurrentDictionary<string, Invoice> Invoices { get; } = new();
    public ConcurrentQueue<InvoiceRequest> Requests { get; } = new();
    private readonly ConcurrentQueue<string> _created = new();
    public bool Fail { get; set; }

    public Task<Invoice> CreateInvoiceAsync(InvoiceRequest r, CancellationToken ct = default)
    {
        if (Fail) throw new HttpRequestException("Moyasar down");
        Requests.Enqueue(r);
        var id = "inv_" + Guid.NewGuid().ToString("N")[..12];
        var inv = new Invoice(id, "initiated", r.Amount, "https://checkout.moyasar.com/invoices/" + id, r.Metadata);
        Invoices[id] = inv;
        _created.Enqueue(id);
        return Task.FromResult(inv);
    }

    public Task<Invoice?> GetInvoiceAsync(string id, CancellationToken ct = default) => Task.FromResult(Invoices.TryGetValue(id, out var i) ? i : null);

    public void Pay(string id) => Invoices[id] = Invoices[id] with { Status = "paid" };

    public string Last(string kind) => _created.Last(id => Invoices[id].Metadata.GetValueOrDefault("kind") == kind);
}
