using System.Globalization;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oxira.Design.Application.Common;
using Oxira.Design.Application.Emails;
using Oxira.Design.Domain;
using static Oxira.Design.Domain.Text;

namespace Oxira.Design.Application.Features.Orders;

/// <summary>A file from a multipart form.</summary>
public record UploadedFile(string Field, string Name, string Mime, byte[] Content);

/// <summary>The order form (multipart). Field names match the website: package, name, phone, email, city, area, style, notes, summary, lang, page, returnUrl, company_website.</summary>
public record OrderInput(IReadOnlyDictionary<string, string> Fields, IReadOnlyList<UploadedFile> Files)
{
    public string? this[string key] => Fields.TryGetValue(key, out var v) ? v : null;
}

public record SubmitOrderCommand(OrderInput Input) : ICommand<OrderReply>;
public record GetPaymentQuery(int OrderId) : IQuery<PayReply>;
/// <summary>called by the payment callback once Moyasar confirmed the invoice</summary>
public record MarkOrderPaidCommand(Invoice Invoice) : ICommand<bool>;

/// <summary>"Orders" section.</summary>
public class OrderOptions
{
    /// <summary>all files of one order together</summary>
    public long MaxFilesBytes { get; set; } = 16 * 1024 * 1024;
    public int MaxFiles { get; set; } = 20;
}

public class OrderHandlers(IAppDbContext db, TimeProvider clock, IEmailSender email, IPayments payments, IFileStore files, IRealtime realtime,
    IOptions<SiteOptions> site, IOptions<OrderOptions> opt, ILogger<OrderHandlers> log)
    : IRequestHandler<SubmitOrderCommand, OrderReply>, IRequestHandler<GetPaymentQuery, PayReply>, IRequestHandler<MarkOrderPaidCommand, bool>
{
    public static readonly string[] Extensions = [".dxf", ".dwg", ".pdf", ".png", ".jpg", ".jpeg", ".webp", ".heic", ".zip", ".glb"];

    public static string Mime(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".heic" => "image/heic",
        ".zip" => "application/zip", ".glb" => "model/gltf-binary", ".dxf" => "application/dxf", ".dwg" => "application/acad", ".ifc" => "application/x-step",
        _ => "application/octet-stream",
    };

    /// <summary>server-side price: the note shown to the team and the client, and what is paid online (SAR incl. VAT)</summary>
    public static (string Note, decimal Amount) Price(string pkg, double area)
    {
        switch (pkg)
        {
            case Packages.Quick:
                var amount = Prices.WithVat(Prices.Quick);
                return ($"سريعة: {Prices.Quick:0} ريال + الضريبة = {Templates.Money(amount)}", amount);
            case Packages.Design:
                var est = Math.Max(Prices.DesignMin, Math.Round((decimal)area * Prices.DesignPerM2));
                return (area > 0 ? $"تصميم داخلي (تقديري): {est:0} ريال + الضريبة لمساحة {area.ToString("0.#", CultureInfo.InvariantCulture)} م²" : "تصميم داخلي: عرض سعر بعد مراجعة المخطط", 0);
            case Packages.Developer: return ($"المطورين: من {Prices.Developer:0} ريال شهرياً + الضريبة", 0);
            case Packages.Plan: return ("تصميم مخطط (صفحة صمّم مخططك): عرض سعر بعد المراجعة", 0);
            case Packages.Free: return ("الاستوديو المجاني: استفسار", 0);
            default: return ("متكاملة: عرض سعر حسب المشروع", 0);
        }
    }

    public async Task<OrderReply> Handle(SubmitOrderCommand cmd, CancellationToken ct)
    {
        var i = cmd.Input;
        if (!string.IsNullOrEmpty(i["company_website"])) return new OrderReply(true, 0); // honeypot: pretend success, store nothing

        var name = Clip(i["name"], 120);
        var phone = Clip(i["phone"], 40);
        if (name == "" || phone.Length <= 4) throw new DomainException("أدخل الاسم ورقم الجوال", "invalid");
        var mail = Clip(i["email"], 160);
        if (mail != "" && !IsEmail(mail)) throw new DomainException("البريد الإلكتروني غير صحيح", "email");

        var pkg = Packages.Of(i["package"]);
        JsonElement? summary = null;
        var summaryText = i["summary"] ?? "";
        if (summaryText.Length is > 0 and <= 30000)
            try { using var d = JsonDocument.Parse(summaryText); if (d.RootElement.ValueKind == JsonValueKind.Object) summary = d.RootElement.Clone(); }
            catch (JsonException) { /* ignore a broken summary */ }

        var area = double.TryParse(i["area"], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : 0;
        if (area != 0 && area is < 10 or > 20000) throw new DomainException("المساحة بين 10 و20,000 م²", "invalid");
        if (area == 0 && summary is { } s0 && s0.TryGetProperty("totalArea", out var ta) && ta.TryGetDouble(out var t)) area = Math.Clamp(t, 0, 20000);
        area = Math.Round(area, 1);

        // files: allowed types only, all together within the limit
        var kept = i.Files.Where(f => Extensions.Contains(Path.GetExtension(f.Name).ToLowerInvariant()) && f.Content.Length > 0).ToList();
        if (kept.Count > opt.Value.MaxFiles || kept.Sum(f => (long)f.Content.Length) > opt.Value.MaxFilesBytes)
            throw DomainException.TooBig("حجم الملفات كبير جدًا (الحد 15 ميجابايت)");

        var (note, amount) = Price(pkg, area);
        var now = clock.GetUtcNow();
        var order = new Order
        {
            Package = pkg, ContactName = name, Phone = phone, Email = mail.ToLowerInvariant(), City = Clip(i["city"], 120), Area = area,
            Style = Clip(i["style"], 20), Notes = Clip(i["notes"], 3000), Summary = summary?.GetRawText(), Rooms = Rooms(summary),
            PriceNote = note, AmountDue = amount, Status = pkg == Packages.Quick ? OrderStatuses.Unpaid : OrderStatuses.New,
            Lang = Langs.Of(i["lang"]), Page = Clip(i["page"], 300), ReturnUrl = site.Value.SafeReturn(i["returnUrl"], site.Value.Site("/")),
            FileNames = Clip(string.Join(", ", kept.Select(f => SafeName(f.Name))), 2000), CreatedAt = now, UpdatedAt = now,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);

        var attachments = new List<EmailAttachment>();
        foreach (var f in kept)
        {
            var fileName = SafeName(f.Name);
            var path = await files.SaveAsync($"orders/{order.Id}", fileName, f.Content, ct);
            var mime = Mime(fileName);
            db.OrderFiles.Add(new OrderFile { OrderId = order.Id, Name = fileName, Mime = mime, Role = "client", Field = Clip(f.Field, 40), Size = f.Content.Length, Path = path, CreatedAt = now });
            attachments.Add(new EmailAttachment(fileName, mime, f.Content));
        }
        if (kept.Count > 0) await db.SaveChangesAsync(ct);

        email.Enqueue(Templates.TeamOrder(order, site.Value.TeamEmail, attachments));
        if (Templates.ClientOrder(order) is { } c) email.Enqueue(c);

        if (pkg != Packages.Quick || amount <= 0) return new OrderReply(true, order.Id);
        try
        {
            var inv = await payments.CreateInvoiceAsync(new InvoiceRequest(Prices.Halalas(amount), $"Oxira Design order #{order.Id} - Quick package",
                $"{order.ReturnUrl}?paid={order.Id}#order", $"{order.ReturnUrl}?cancel={order.Id}#order",
                new Dictionary<string, string> { ["order_id"] = order.Id.ToString(CultureInfo.InvariantCulture), ["kind"] = PaymentKinds.Design }), ct);
            order.InvoiceId = inv.Id;
            await db.SaveChangesAsync(ct);
            return new OrderReply(true, order.Id, inv.Url);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // the order is saved and the team has it; the client can pay later through the team
            log.LogError(e, "Moyasar invoice for order {Id} failed", order.Id);
            return new OrderReply(true, order.Id);
        }
    }

    public static string SafeName(string name)
    {
        var n = Path.GetFileName(name.Replace('\\', '/'));
        n = new string(n.Select(ch => char.IsControl(ch) || Path.GetInvalidFileNameChars().Contains(ch) || ch is '"' or '<' or '>' ? '_' : ch).ToArray()).Trim(' ', '.');
        return Clip(n == "" ? "file" : n, 120);
    }

    private static string Rooms(JsonElement? summary)
    {
        if (summary is not { } s || !s.TryGetProperty("rooms", out var rooms) || rooms.ValueKind != JsonValueKind.Array) return "";
        string S(JsonElement r, string k, int n) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(k, out var v) ? Clip(v.ToString(), n) : "";
        double A(JsonElement r) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("area", out var v) && v.TryGetDouble(out var d) ? d : 0;
        return Clip(string.Join("، ", rooms.EnumerateArray().Take(40).Select(r => $"{S(r, "name", 40)} ({S(r, "type", 12)}) {A(r).ToString("0.0", CultureInfo.InvariantCulture)} م²")), 4000);
    }

    public async Task<PayReply> Handle(GetPaymentQuery q, CancellationToken ct)
    {
        var o = await db.Orders.AsNoTracking().Where(x => x.Id == q.OrderId).Select(x => new { x.Id, x.PaidAt }).FirstOrDefaultAsync(ct);
        return o == null ? new PayReply(false, null, false, "not_found") : new PayReply(true, o.Id, o.PaidAt != null);
    }

    public async Task<bool> Handle(MarkOrderPaidCommand cmd, CancellationToken ct)
    {
        var inv = cmd.Invoice;
        if (!int.TryParse(inv.Metadata.GetValueOrDefault("order_id"), out var id)) return false;
        var now = clock.GetUtcNow();
        // once only: the row changes only while it is unpaid
        var changed = await db.Orders.Where(o => o.Id == id && o.InvoiceId == inv.Id && o.PaidAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.PaidAt, now).SetProperty(o => o.UpdatedAt, now)
                .SetProperty(o => o.Status, o => o.Status == OrderStatuses.Unpaid || o.Status == OrderStatuses.New ? OrderStatuses.Paid : o.Status), ct);
        if (changed == 0) return false;
        var order = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == id, ct);
        foreach (var m in Templates.OrderPaid(order, inv, site.Value.TeamEmail)) email.Enqueue(m);
        await realtime.OrderAsync(new OrderEvent(order.Id, "paid", order.Status));
        return true;
    }
}
