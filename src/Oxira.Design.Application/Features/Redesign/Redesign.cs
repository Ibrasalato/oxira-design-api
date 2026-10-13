using System.Globalization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oxira.Design.Application.Common;
using Oxira.Design.Application.Emails;
using Oxira.Design.Application.Features.Renders;
using Oxira.Design.Domain;
using static Oxira.Design.Domain.Text;

namespace Oxira.Design.Application.Features.Redesign;

/// <summary>A photo of a real room (data URL ≤ 3 MB of base64) redesigned in a style.</summary>
public record RoomInput(string? SessionId, string? Wallet, string? Style, string? Room, string? Lang, string? Image);
public record PurchaseInput(string? Wallet, string? Pack, string? Email, string? Lang, string? ReturnUrl);
public record PurchaseReply(bool Success, string? PayUrl, int? PurchaseId = null, string? Reason = null) : IReply;

public record RedesignCommand(RoomInput Input) : ICommand<RoomReply>;
public record WalletQuery(string? Wallet, string? SessionId) : IQuery<WalletReply>;
public record BuyPackCommand(PurchaseInput Input) : ICommand<PurchaseReply>;
/// <summary>called by the payment callback once Moyasar confirmed the invoice</summary>
public record CreditPurchaseCommand(Invoice Invoice) : ICommand<bool>;

/// <summary>
/// Room redesign: free renders per visitor, per IP and per day on the cheap model; then one wallet credit per render on the
/// higher-fidelity model. A failed paid render gives the credit back.
/// </summary>
public class RedesignHandlers(IAppDbContext db, TimeProvider clock, IQuota quota, IImageEditor images, ICurrentUser me, Wallets wallets,
    IPayments payments, IEmailSender email, IOptions<RedesignOptions> opt, IOptions<SiteOptions> site, ILogger<RedesignHandlers> log)
    : IRequestHandler<RedesignCommand, RoomReply>, IRequestHandler<WalletQuery, WalletReply>, IRequestHandler<BuyPackCommand, PurchaseReply>,
      IRequestHandler<CreditPurchaseCommand, bool>
{
    public static readonly Dictionary<string, string> Styles = new()
    {
        ["modern"] = "modern minimalist: warm white walls, light oak wood, grey fabric upholstery, black metal details, clean lines, hidden LED lighting",
        ["classic"] = "classic elegant: cream walls with wall mouldings, walnut wood, marble, burgundy and gold upholstery, crystal chandelier",
        ["najdi"] = "contemporary Najdi Saudi: earthy beige plaster walls, terracotta and stone, low Arabic majlis seating with red patterned cushions, carved dark wood, brass lanterns",
        ["scandi"] = "Scandinavian: white walls, pale ash wood, light grey and beige textiles, plants, airy and bright",
        ["luxury"] = "luxury: polished marble, warm grey walls, dark wood panels, emerald velvet, gold accents, designer lighting",
    };

    public static readonly Dictionary<string, string> Rooms = new()
    {
        ["living"] = "living room", ["bedroom"] = "bedroom", ["majlis"] = "majlis (Arabic guest sitting room)", ["kitchen"] = "kitchen",
        ["bathroom"] = "bathroom", ["dining"] = "dining room", ["office"] = "home office", ["kids"] = "children's bedroom",
    };

    public static string Prompt(string room, string style) =>
        $"Redesign this photo of a real {Rooms[room]} as a professionally designed interior. Keep exactly the same room: the same walls, windows, doors, ceiling height, floor area, camera position and perspective. Only change finishes, furniture, lighting and decor. Style: {Styles[style]}. Photorealistic high-end interior photography, natural daylight, realistic materials and proportions. No people, no text, no watermark.";

    private (string Key, int Limit)[] FreeKeys(string sid) =>
        [($"room:s:{sid}", opt.Value.FreePerVisitor), ($"room:ip:{me.Ip}", opt.Value.FreePerIp), ("room:all", opt.Value.FreePerDay)];

    private async Task<int> FreeLeftAsync(string sid, CancellationToken ct)
    {
        var left = int.MaxValue;
        foreach (var (key, limit) in FreeKeys(sid)) left = Math.Min(left, limit - await quota.UsedAsync(key, ct));
        return Math.Max(0, left);
    }

    public async Task<RoomReply> Handle(RedesignCommand cmd, CancellationToken ct)
    {
        var i = cmd.Input;
        var o = opt.Value;
        var sid = SessionKey(i.SessionId);
        var image = DataUrl.Parse(i.Image, o.MaxImageChars);
        if (sid.Length < 6 || image == null) return new RoomReply(false, null, 0, null, "invalid");
        var wallet = IsWallet(i.Wallet) ? i.Wallet! : "";
        var style = Styles.ContainsKey(i.Style ?? "") ? i.Style! : "modern";
        var room = Rooms.ContainsKey(i.Room ?? "") ? i.Room! : "living";

        var taken = new List<string>();
        foreach (var (key, limit) in FreeKeys(sid))
        {
            if (await quota.TryConsumeAsync(key, limit, ct) == null)
            {
                foreach (var k in taken) await quota.RefundAsync(k, ct);
                taken.Clear();
                break;
            }
            taken.Add(key);
        }
        var paid = taken.Count == 0;
        int? credits = null;
        if (paid)
        {
            credits = wallet == "" ? null : await wallets.SpendAsync(wallet, ct);
            if (credits == null) return new RoomReply(false, null, 0, 0, "credits");
        }
        var freeLeft = await FreeLeftAsync(sid, ct);

        var entry = new RenderLog { Kind = "room", SessionId = sid, Ip = me.Ip, Style = style, View = room, Mode = paid ? "paid" : "free", Lang = Langs.Of(i.Lang), CreatedAt = clock.GetUtcNow() };
        try
        {
            var jpeg = await images.EditAsync(new ImageEdit([image], Prompt(room, style), paid ? o.PaidModel : o.FreeModel, "auto", o.Quality, o.Compression,
                paid ? o.PaidFidelity : o.FreeFidelity), ct);
            entry.Ok = true;
            db.RenderLogs.Add(entry);
            await db.SaveChangesAsync(ct);
            return new RoomReply(true, DataUrl.Jpeg(jpeg), freeLeft, credits);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Room render failed for {Session}", sid);
            foreach (var k in taken) await quota.RefundAsync(k, ct);
            if (paid) credits = await wallets.RefundAsync(wallet, ct);
            db.RenderLogs.Add(entry);
            await db.SaveChangesAsync(ct);
            return new RoomReply(false, null, await FreeLeftAsync(sid, ct), credits, "failed");
        }
    }

    public async Task<WalletReply> Handle(WalletQuery q, CancellationToken ct)
    {
        var credits = IsWallet(q.Wallet) ? await wallets.BalanceAsync(q.Wallet!, ct) : 0;
        return new WalletReply(true, credits, await FreeLeftAsync(SessionKey(q.SessionId), ct));
    }

    public async Task<PurchaseReply> Handle(BuyPackCommand cmd, CancellationToken ct)
    {
        var i = cmd.Input;
        var pack = Prices.Packs.FirstOrDefault(p => p.Id == i.Pack);
        if (!IsWallet(i.Wallet) || pack == null) return new PurchaseReply(false, null, null, "invalid");
        var lang = Langs.Of(i.Lang);
        var ret = (i.ReturnUrl ?? "").Split('?')[0].Split('#')[0];
        ret = site.Value.SafeReturn(ret, site.Value.Site(Langs.Prefix(lang) + "/redesign/"));
        var p = new Purchase { WalletId = i.Wallet!, Pack = pack.Id, Renders = pack.Renders, Amount = pack.Price, Email = Email(i.Email), Lang = lang, CreatedAt = clock.GetUtcNow() };
        db.Purchases.Add(p);
        await db.SaveChangesAsync(ct);
        try
        {
            var inv = await payments.CreateInvoiceAsync(new InvoiceRequest(Prices.Halalas(pack.Price), $"Oxira Design - {pack.Renders} AI room renders (#{p.Id})",
                $"{ret}?bought={p.Id}", $"{ret}?cancel=1",
                new Dictionary<string, string> { ["kind"] = PaymentKinds.Redesign, ["purchase_id"] = p.Id.ToString(CultureInfo.InvariantCulture), ["wallet"] = p.WalletId }), ct);
            p.InvoiceId = inv.Id;
            await db.SaveChangesAsync(ct);
            return new PurchaseReply(true, inv.Url, p.Id);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Moyasar invoice for purchase {Id} failed", p.Id);
            return new PurchaseReply(false, null, p.Id, "payments");
        }
    }

    public async Task<bool> Handle(CreditPurchaseCommand cmd, CancellationToken ct)
    {
        var inv = cmd.Invoice;
        if (!int.TryParse(inv.Metadata.GetValueOrDefault("purchase_id"), out var id)) return false;
        var now = clock.GetUtcNow();
        var changed = await db.Purchases.Where(p => p.Id == id && p.InvoiceId == inv.Id && p.Status == "pending")
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "paid").SetProperty(p => p.PaidAt, now), ct);
        if (changed == 0) return false;
        var p = await db.Purchases.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        var balance = await wallets.CreditAsync(p.WalletId, p.Renders, p.Email, ct);
        foreach (var m in Templates.PackBought(p, balance, site.Value)) email.Enqueue(m);
        return true;
    }
}
