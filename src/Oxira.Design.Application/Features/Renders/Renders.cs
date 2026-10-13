using System.Text.RegularExpressions;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oxira.Design.Application.Common;
using Oxira.Design.Application.Features.Redesign;
using Oxira.Design.Domain;
using static Oxira.Design.Domain.Text;

namespace Oxira.Design.Application.Features.Renders;

/// <summary>
/// A studio render. image: data URL of the 3D view (≤ 4 MB of base64); source: snapshot (viewport) or traced (path-traced frame);
/// ref: an earlier render of the same home and style, so views match; wallet: paid credits once the free renders are used.
/// </summary>
public record RenderInput(string? SessionId, string? Image, string? Style, string? View, string? Room, string? Lang, string? Source, string? Ref, string? Wallet);

public record RenderCommand(RenderInput Input) : ICommand<RenderReply>;

public static partial class DataUrl
{
    [GeneratedRegex(@"^data:image/(jpeg|png|webp);base64,([A-Za-z0-9+/=]+)$")] private static partial Regex Re();

    /// <returns>the decoded image, or null when it is not a JPEG/PNG/WebP data URL within maxChars of base64</returns>
    public static ImageInput? Parse(string? url, int maxChars)
    {
        if (string.IsNullOrEmpty(url) || url.Length > maxChars + 40) return null;
        var m = Re().Match(url);
        if (!m.Success || m.Groups[2].Length > maxChars) return null;
        try { return new ImageInput(Convert.FromBase64String(m.Groups[2].Value), "image/" + m.Groups[1].Value); }
        catch (FormatException) { return null; }
    }

    public static string Jpeg(byte[] bytes) => "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
}

/// <summary>
/// Free renders: per visitor (session), per IP and per day. After that a visitor with a wallet pays one credit (shared with the
/// room-redesign packs). A failed render gives back what it took.
/// </summary>
public class RenderHandlers(IAppDbContext db, TimeProvider clock, IQuota quota, IImageEditor images, ICurrentUser me, Wallets wallets,
    IOptions<RenderOptions> opt, ILogger<RenderHandlers> log) : IRequestHandler<RenderCommand, RenderReply>
{
    public static readonly Dictionary<string, string> Styles = new()
    {
        ["modern"] = "modern minimalist interior: warm white walls, light oak wood floors with visible grain, grey boucle fabric sofa, black metal details, linen curtains, clean lines",
        ["classic"] = "classic elegant interior: cream walls with mouldings, dark walnut wood floors, marble, burgundy upholstery, brass details, silk curtains",
        ["najdi"] = "contemporary Najdi Saudi interior: earthy beige plaster walls, terracotta tiles, traditional Arabic majlis floor seating with red patterned cushions, carved dark wood doors, warm lantern lighting",
        ["scandi"] = "Scandinavian interior: white walls, pale ash wood floors, light grey textiles, wool throws, potted plants, airy and bright",
        ["luxury"] = "luxury interior: polished white marble floors with soft reflections, warm grey walls, dark wood panels, emerald velvet, brushed gold details, designer lighting",
    };

    public static readonly Dictionary<string, string> Views = new()
    {
        ["top"] = "aerial cutaway dollhouse view looking down at the whole home, walls cut at mid height",
        ["orbit"] = "three-quarter aerial dollhouse view of the home with the roof removed",
        ["walk"] = "eye-level interior photograph inside the room, two-point perspective with vertical lines kept vertical",
    };

    private const string Corona = "Final look: a high-end architectural visualization as produced with Corona Renderer in 3ds Max: physically accurate global illumination, soft bounced light in the shadows, warm sun patches with crisp contact shadows, gentle bloom and glare around bright windows, true-to-life PBR materials with fine surface detail (wood grain, fabric weave, subtle floor reflections), natural white balance, filmic contrast, crisp and noise-free. ";
    private const string Keep = "Keep exactly the same camera position, angle and perspective, the same walls, doors, windows, room layout and furniture positions; do not add or remove rooms, openings or major furniture and do not change proportions. ";
    private const string RefNote = "The second image is an approved render of another view of this same home in this style: use exactly the same furniture designs, upholstery fabrics and colours, wood tones, floor finish, curtains and decor style as in it, so both images look like one project. Do not copy its camera angle or layout. ";

    public static string Prompt(string style, string view, bool traced, bool hasRef) => (hasRef ? RefNote : "") + (traced
        ? "This image is a physically based path-traced render of a 3D model of a home; its lighting, sun direction and shadows are correct. Finish it into a photorealistic render. " + Keep +
          $"Keep the direction of the sunlight and shadows. View: {Views[view]}. Style: {Styles[style]}. Add tasteful realistic decor consistent with the style (rugs, cushions, books, plants, art) without moving the furniture. " + Corona + "No people, no text, no labels, no watermark."
        : "Turn this rough 3D model screenshot into a photorealistic architectural render. " + Keep + $"View: {Views[view]}. Style: {Styles[style]}. " + Corona +
          "Soft natural daylight. Remove any labels or interface elements. No people, no text, no watermark.");

    public async Task<RenderReply> Handle(RenderCommand cmd, CancellationToken ct)
    {
        var i = cmd.Input;
        var o = opt.Value;
        var sid = SessionKey(i.SessionId);
        var image = DataUrl.Parse(i.Image, o.MaxImageChars);
        if (sid.Length < 6 || image == null) return new RenderReply(false, null, 0, null, "invalid");
        var refImage = DataUrl.Parse(i.Ref, o.MaxRefChars);
        var wallet = IsWallet(i.Wallet) ? i.Wallet! : "";
        var style = Styles.ContainsKey(i.Style ?? "") ? i.Style! : "modern";
        var view = Views.ContainsKey(i.View ?? "") ? i.View! : "orbit";
        var traced = i.Source == "traced";

        // free first; then a wallet credit
        var keys = new[] { ($"render:s:{sid}", o.PerSession), ($"render:ip:{me.Ip}", o.PerIp), ("render:all", o.PerDay) };
        var taken = new List<string>();
        int? used = null;
        foreach (var (key, limit) in keys)
        {
            var n = await quota.TryConsumeAsync(key, limit, ct);
            if (n == null)
            {
                foreach (var k in taken) await quota.RefundAsync(k, ct);
                taken.Clear();
                used = null;
                break;
            }
            used ??= n;
            taken.Add(key);
        }
        var paid = taken.Count == 0;
        int? credits = null;
        if (paid)
        {
            if (wallet == "") return new RenderReply(false, null, 0, null, "limit");
            credits = await wallets.SpendAsync(wallet, ct);
            if (credits == null) return new RenderReply(false, null, 0, 0, "credits");
        }
        var remaining = paid ? 0 : Math.Max(0, o.PerSession - used!.Value);

        var log1 = new RenderLog { Kind = "studio", SessionId = sid, Ip = me.Ip, Style = style, View = view, Mode = paid ? "paid" : "free", Lang = Langs.Of(i.Lang), CreatedAt = clock.GetUtcNow() };
        try
        {
            var inputs = refImage != null ? new[] { image, refImage } : new[] { image };
            var jpeg = await images.EditAsync(new ImageEdit(inputs, Prompt(style, view, traced, refImage != null), o.Model, o.Size, traced ? "high" : "medium", o.Compression), ct);
            log1.Ok = true;
            db.RenderLogs.Add(log1);
            await db.SaveChangesAsync(ct);
            return new RenderReply(true, DataUrl.Jpeg(jpeg), remaining, credits);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Studio render failed for {Session}", sid);
            foreach (var k in taken) await quota.RefundAsync(k, ct);
            if (paid) credits = await wallets.RefundAsync(wallet, ct);
            db.RenderLogs.Add(log1);
            await db.SaveChangesAsync(ct);
            return new RenderReply(false, null, paid ? 0 : remaining + 1, credits, "failed");
        }
    }
}
