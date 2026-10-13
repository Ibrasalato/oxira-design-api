using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Oxira.Design.Application.Common;

namespace Oxira.Design.Infrastructure.Services;

/// <summary>
/// "Payments" section. Moyasar invoices: the secret key (Payments__SecretKey) is the Basic-auth user name.
/// Simulate = true (development, demos before the Moyasar account is live) creates local invoices that are paid by opening their link.
/// </summary>
public class PaymentOptions
{
    public bool Simulate { get; set; }
    public string SecretKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.moyasar.com";
}

public class MoyasarPayments(HttpClient http, IOptions<PaymentOptions> opt, IOptions<SiteOptions> site) : IPayments
{
    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var o = opt.Value;
        if (string.IsNullOrEmpty(o.SecretKey)) throw new InvalidOperationException("Payments:SecretKey is not set");
        var req = new HttpRequestMessage(method, $"{o.BaseUrl.TrimEnd('/')}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(o.SecretKey + ":")));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    public async Task<Invoice> CreateInvoiceAsync(InvoiceRequest r, CancellationToken ct = default)
    {
        var metadata = new JsonObject();
        foreach (var (k, v) in r.Metadata) metadata[k] = v;
        var body = new JsonObject
        {
            ["amount"] = r.Amount, ["currency"] = "SAR", ["description"] = r.Description,
            ["callback_url"] = site.Value.Api("/api/payments/moyasar/callback"),
            ["success_url"] = r.SuccessUrl, ["back_url"] = r.BackUrl, ["metadata"] = metadata,
        };
        using var req = Request(HttpMethod.Post, "/v1/invoices");
        req.Content = JsonContent.Create(body);
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Moyasar {(int)res.StatusCode}: {text[..Math.Min(text.Length, 400)]}");
        return Parse(text);
    }

    public async Task<Invoice?> GetInvoiceAsync(string id, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Get, "/v1/invoices/" + Uri.EscapeDataString(id));
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Moyasar {(int)res.StatusCode}: {text[..Math.Min(text.Length, 400)]}");
        return Parse(text);
    }

    public static Invoice Parse(string json)
    {
        using var d = JsonDocument.Parse(json);
        var e = d.RootElement;
        string S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var meta = new Dictionary<string, string>();
        if (e.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object)
            foreach (var p in m.EnumerateObject()) meta[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
        var amount = e.TryGetProperty("amount", out var a) && a.TryGetInt32(out var n) ? n : 0;
        return new Invoice(S("id"), S("status"), amount, S("url") is { Length: > 0 } u ? u : null, meta);
    }
}

/// <summary>Opening a simulated invoice's link pays it (see PaymentsController): no money moves.</summary>
public interface ISimulatedCheckout
{
    /// <returns>where to send the browser next, or null when the invoice is unknown</returns>
    string? Pay(string id);
}

public class SimulatedPayments(IOptions<SiteOptions> site) : IPayments, ISimulatedCheckout
{
    private record Entry(InvoiceRequest Request, string Status);
    private static readonly ConcurrentDictionary<string, Entry> Invoices = new();

    public Task<Invoice> CreateInvoiceAsync(InvoiceRequest r, CancellationToken ct = default)
    {
        var id = "sim_" + Guid.NewGuid().ToString("N")[..20];
        Invoices[id] = new Entry(r, "initiated");
        return Task.FromResult(new Invoice(id, "initiated", r.Amount, site.Value.Api($"/api/payments/simulate/{id}"), r.Metadata));
    }

    public Task<Invoice?> GetInvoiceAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(Invoices.TryGetValue(id, out var e) ? new Invoice(id, e.Status, e.Request.Amount, null, e.Request.Metadata) : null);

    public string? Pay(string id)
    {
        if (!Invoices.TryGetValue(id, out var e)) return null;
        Invoices[id] = e with { Status = "paid" };
        return e.Request.SuccessUrl;
    }
}
