using System.Text.Json;
using System.Text.Json.Nodes;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oxira.Design.Application.Common;
using Oxira.Design.Application.Features.Renders;
using Oxira.Design.Domain;
using static Oxira.Design.Domain.Text;

namespace Oxira.Design.Application.Features.PlanAi;

/// <summary>
/// kind "brief": text (4–2000 characters) + current settings (brief) → a settings patch for the plan generator and a short reply.
/// kind "image": a floor-plan image (data URL ≤ 5.5 MB of base64; PDF pages are rendered in the browser) → room rectangles in metres.
/// </summary>
public record PlanAiInput(string? Kind, string? Text, string? Lang, string? Region, JsonElement? Brief, string? Image, double? KnownWidth);
public record PlanRoom(string Name, string Type, double X, double Y, double W, double H);
public record PlanAiReply(bool Success, string Kind, JsonElement? Patch = null, string? Reply = null, double? Width = null, double? Depth = null,
    List<PlanRoom>? Rooms = null, string? Notes = null, string? Reason = null) : IReply;

public record PlanAiCommand(PlanAiInput Input) : ICommand<PlanAiReply>;

public class PlanAiHandlers(IAnthropic ai, IQuota quota, ICurrentUser me, IOptions<PlanAiOptions> opt, ILogger<PlanAiHandlers> log)
    : IRequestHandler<PlanAiCommand, PlanAiReply>
{
    public const string Kinds = "entrance, hall, living, majlis, ladies, dining, kitchen, master, bedroom, guest, bath, wc, dress, maid, driver, laundry, store, office, prayer, stair, lift, landing, shop, parking, terrace, void, garage";

    public static string BriefSystem(string lang, string region) =>
        "You set up an automatic floor-plan generator from what a client says about the house they want. " +
        "Reply with ONE JSON object and nothing else: {\"patch\": {...}, \"reply\": \"...\"}. " +
        "\"patch\" contains only the settings the client actually implied, in this shape (all lengths in metres; convert feet with 1 ft = 0.3048 m): " +
        "{\"type\": \"villa\"|\"duplex\"|\"building\"|\"mixed\"|\"istiraha\", \"land\": {\"w\": number (street frontage), \"d\": number (depth), \"streets\": 1-4, \"streetW\": number}, " +
        "\"villa\": {\"floors\": 1-4 including ground, \"roof\": bool (roof annex), \"bedrooms\": 0-10 total, \"masters\": 0-4, \"ensuiteAll\": bool, \"baths\": 0-4 extra shared bathrooms, \"majlis\": bool, \"ladies\": bool, \"dining\": bool, \"kitchen\": \"open\"|\"closed\", \"maid\": bool, \"driver\": bool, \"laundry\": bool, \"store\": bool, \"office\": bool, \"guestBed\": bool (ground-floor guest bedroom), \"prayer\": bool, \"lift\": bool, \"garage\": bool}, " +
        "\"bld\": {\"floors\": 1-20 typical floors, \"perFloor\": 1-4 apartments per floor, \"beds\": 1-5 bedrooms per apartment, \"majlis\": bool, \"maid\": bool, \"ground\": \"parking\"|\"shops\"|\"apartments\", \"roof\": bool, \"lift\": bool}, " +
        "\"planTypes\": array of \"arch\",\"facade\",\"structural\",\"electrical\",\"plumbing\",\"hvac\",\"exterior3d\",\"interior\",\"permit\",\"landscape\", \"facade\": \"modern\"|\"classic\"|\"najdi\"|\"neoclassic\"|\"none\"}. " +
        $"If the client gives only a plot area, choose a sensible width and depth for it. \"reply\" is one or two short sentences in the client's language (page language: {lang}) saying what you set and what is still open. " +
        $"Never invent personal details. Country of the client: {(region == "" ? "unknown" : region)}.";

    public static string ImageSystem(string lang, double knownWidth) =>
        "You read architectural floor plans from images. Reply with ONE JSON object and nothing else: " +
        "{\"width\": number, \"depth\": number, \"rooms\": [{\"name\": string, \"type\": string, \"x\": number, \"y\": number, \"w\": number, \"h\": number}], \"notes\": string}. " +
        "Measure the main building outline only (one floor; if several floors are shown, use the ground floor). Units are metres. " +
        "Origin is the bottom-left corner of the building outline, x grows to the right, y grows towards the TOP of the image. " +
        "The rooms must tile the whole width x depth rectangle with no gaps and no overlaps: include halls and corridors (type hall), stairs (stair), balconies (terrace) and small rooms. Use wall centre lines. " +
        "Use the dimension annotations and the scale written on the drawing; if there are none, assume typical sizes (a door is about 0.9 m, a bedroom about 3.5 to 4.5 m wide). " +
        (knownWidth > 0 ? $"The client says the building is {knownWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)} m wide; use that for scale. " : "") +
        $"\"type\" is one of: {Kinds}. Keep \"name\" as written on the plan (any language), or a short name if none. " +
        $"\"notes\" is one short sentence in language \"{lang}\" about anything uncertain.";

    public async Task<PlanAiReply> Handle(PlanAiCommand cmd, CancellationToken ct)
    {
        var i = cmd.Input;
        var o = opt.Value;
        var kind = i.Kind == "image" ? "image" : "brief";
        var lang = Clip(i.Lang, 5) is { Length: > 0 } l ? l : "en";
        var region = new string((i.Region ?? "").Where(char.IsAsciiLetterUpper).Take(2).ToArray());

        // validate before counting
        AiRequest request;
        if (kind == "brief")
        {
            var text = Clip(i.Text, 2000);
            if (text.Length < 4) return new PlanAiReply(false, kind, Reason: "empty");
            var current = i.Brief is { ValueKind: JsonValueKind.Object } b ? b.GetRawText() : "{}";
            if (current.Length > 4000) current = "{}";
            request = new AiRequest(BriefSystem(lang, region), [new JsonObject { ["role"] = "user", ["content"] = $"Current settings: {current}\n\nClient: {text}" }], null, 4000, o.Model);
        }
        else
        {
            var url = i.Image ?? "";
            var image = DataUrl.Parse(url, o.MaxImageChars);
            if (image == null) return new PlanAiReply(false, kind, Reason: "image");
            var knownWidth = i.KnownWidth is > 2 and < 300 ? i.KnownWidth.Value : 0;
            var content = new JsonArray
            {
                new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.Mime, ["data"] = url[(url.IndexOf(',') + 1)..] } },
                new JsonObject { ["type"] = "text", ["text"] = "Read this floor plan and return the JSON." },
            };
            request = new AiRequest(ImageSystem(lang, knownWidth), [new JsonObject { ["role"] = "user", ["content"] = content }], null, 12000, o.Model);
        }

        var (perIp, perDay) = kind == "brief" ? (o.BriefsPerIp, o.BriefsPerDay) : (o.ImagesPerIp, o.ImagesPerDay);
        var ipKey = $"planai:{kind}:ip:{me.Ip}";
        if (await quota.TryConsumeAsync(ipKey, perIp, ct) == null) return new PlanAiReply(false, kind, Reason: "limit");
        if (await quota.TryConsumeAsync($"planai:{kind}:all", perDay, ct) == null)
        {
            await quota.RefundAsync(ipKey, ct);
            return new PlanAiReply(false, kind, Reason: "limit");
        }

        string answer;
        try { answer = (await ai.CreateMessageAsync(request, ct)).Text; }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Plan AI ({Kind}) failed", kind);
            await quota.RefundAsync(ipKey, ct);
            await quota.RefundAsync($"planai:{kind}:all", ct);
            return new PlanAiReply(false, kind, Reason: "ai");
        }

        JsonElement data;
        try
        {
            using var doc = JsonDocument.Parse(JsonObjectIn(answer) ?? "");
            data = doc.RootElement.Clone();
        }
        catch (JsonException) { return new PlanAiReply(false, kind, Reason: "parse"); }
        if (data.ValueKind != JsonValueKind.Object) return new PlanAiReply(false, kind, Reason: "parse");

        if (kind == "brief")
        {
            var patch = data.TryGetProperty("patch", out var p) && p.ValueKind == JsonValueKind.Object ? p : JsonDocument.Parse("{}").RootElement.Clone();
            var reply = data.TryGetProperty("reply", out var r) ? Clip(r.ToString(), 500) : "";
            return new PlanAiReply(true, kind, patch, reply);
        }

        static double Num(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.TryGetDouble(out var d) ? Math.Round(d, 2) : double.NaN;
        static string Str(JsonElement e, string k, int n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) ? Clip(v.ToString(), n) : "";
        IEnumerable<JsonElement> list = data.TryGetProperty("rooms", out var rs) && rs.ValueKind == JsonValueKind.Array ? rs.EnumerateArray() : [];
        var rooms = list.Take(80)
            .Select(x => new PlanRoom(Str(x, "name", 40), Str(x, "type", 12), Num(x, "x"), Num(x, "y"), Num(x, "w"), Num(x, "h")))
            .Where(x => double.IsFinite(x.X) && double.IsFinite(x.Y) && double.IsFinite(x.W) && double.IsFinite(x.H) && x.W > 0.3 && x.H > 0.3)
            .ToList();
        var width = Num(data, "width");
        var depth = Num(data, "depth");
        if (!(width is > 2 and < 300 && depth is > 2 and < 300) || rooms.Count < 1) return new PlanAiReply(false, kind, Reason: "unreadable");
        return new PlanAiReply(true, kind, Width: width, Depth: depth, Rooms: rooms, Notes: Str(data, "notes", 300));
    }
}
