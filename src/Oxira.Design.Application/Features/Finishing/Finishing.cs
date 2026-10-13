using System.Globalization;
using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Options;
using Oxira.Design.Application.Common;
using Oxira.Design.Application.Emails;
using Oxira.Design.Domain;
using static Oxira.Design.Domain.Text;

namespace Oxira.Design.Application.Features.Finishing;

/// <summary>A finishing-quote request from the studio: contact + the plan's quantities (boq: floor, paint, tiles, ceiling, skirting) and rooms.</summary>
public record FinishingInput(string? Name, string? Phone, string? Email, string? City, string? Level, string? Notes, string? Lang, string? File,
    double? Area, JsonElement? Boq, JsonElement? Rooms);

public record FinishingCommand(FinishingInput Input) : ICommand<IdReply>;

public class FinishingHandlers(IAppDbContext db, TimeProvider clock, IQuota quota, IEmailSender email, ICurrentUser me, IOptions<FormLimitOptions> opt, IOptions<SiteOptions> site)
    : IRequestHandler<FinishingCommand, IdReply>
{
    public static readonly Dictionary<string, string> Levels = new() { ["economy"] = "Economy", ["standard"] = "Standard", ["premium"] = "Premium" };

    /// <summary>a sane quantity (0 … 100 000, one decimal)</summary>
    public static double Num(double? v) => v is { } x && double.IsFinite(x) && x is >= 0 and < 100000 ? Math.Round(x, 1) : 0;
    private static double Num(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.TryGetDouble(out var d) ? Num(d) : 0;
    private static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    public async Task<IdReply> Handle(FinishingCommand cmd, CancellationToken ct)
    {
        var i = cmd.Input;
        var name = Clean(i.Name, 80);
        var phone = Clean(i.Phone, 30);
        var area = Num(i.Area);
        var level = Levels.ContainsKey(i.Level ?? "") ? i.Level! : "standard";
        var boq = i.Boq ?? default;
        var (floor, paint, tiles, ceiling, skirting) = (Num(boq, "floor"), Num(boq, "paint"), Num(boq, "tiles"), Num(boq, "ceiling"), Num(boq, "skirting"));
        var rooms = (i.Rooms is { ValueKind: JsonValueKind.Array } r ? r.EnumerateArray().Take(60).ToList() : new List<JsonElement>())
            .Select(x => (Name: Str(x, "name", 40), Type: Str(x, "type", 20), Area: Num(x, "area"))).ToList();
        if (name == "" || !IsPhone(phone) || area == 0 || rooms.Count == 0) return new IdReply(false, null, "invalid");
        if (await quota.TryConsumeAsync($"finishing:ip:{me.Ip}", opt.Value.FinishingPerIp, ct) == null) return new IdReply(false, null, "limit");

        var notes = Clean(i.Notes, 1000);
        var file = Clean(i.File, 120);
        var summary = $"Finishing quote ({Levels[level]}), {N(area)} m2, {rooms.Count} rooms. Floor {N(floor)} m2, paint {N(paint)} m2, wet-area tiles {N(tiles)} m2, ceiling {N(ceiling)} m2, skirting {N(skirting)} m." +
            (notes != "" ? " Notes: " + notes : "");
        var lead = new Lead
        {
            ContactName = name, Phone = phone, Email = Email(i.Email), City = Clean(i.City, 60), Package = "finishing", Area = N(area), Needs = summary,
            PreferredContact = "whatsapp", Channel = LeadChannels.Studio, Lang = Langs.Of(i.Lang), Page = "/studio/", CreatedAt = clock.GetUtcNow(),
            Context = Clip(JsonSerializer.Serialize(new { boq = new { floor, paint, tiles, ceiling, skirting }, rooms = rooms.Select(x => new { name = x.Name, type = x.Type, area = x.Area }), file }), 4000),
        };
        db.Leads.Add(lead);
        await db.SaveChangesAsync(ct);

        static string Row(string k, string v) => $"<tr><td style=\"padding:4px 12px 4px 0;color:#556779\">{Html(k)}</td><td style=\"padding:4px 0\"><b>{Html(v)}</b></td></tr>";
        var html = "<div style=\"font-family:Arial,sans-serif;color:#0A253E\"><h2>New finishing quote request</h2><table>" +
            Row("Name", name) + Row("Phone", phone) + Row("Email", lead.Email == "" ? "-" : lead.Email) + Row("City", lead.City == "" ? "-" : lead.City) +
            Row("Finish level", Levels[level]) + Row("Language", lead.Lang) + Row("Plan file", file == "" ? "-" : file) +
            "</table><h3>Quantities (estimate from the studio)</h3><table>" + Row("Total area", N(area) + " m2") + Row("Flooring (+10% waste)", N(floor) + " m2") +
            Row("Wall paint", N(paint) + " m2") + Row("Wet-area wall tiles", N(tiles) + " m2") + Row("Ceiling", N(ceiling) + " m2") + Row("Skirting", N(skirting) + " m") +
            "</table><h3>Rooms</h3><table>" + string.Concat(rooms.Select(x => Row($"{(x.Name == "" ? x.Type : x.Name)} ({x.Type})", N(x.Area) + " m2"))) + "</table>" +
            (notes != "" ? "<h3>Notes</h3>" + Templates.P(notes) : "") +
            $"<p style=\"color:#556779\">Reply on WhatsApp: https://wa.me/{Digits(phone)}</p></div>";
        email.Enqueue(new EmailMessage([site.Value.TeamEmail], $"Finishing quote: {name} - {N(area)} m2 ({Levels[level]}) #{lead.Id}", html, lead.Email == "" ? null : lead.Email));
        return new IdReply(true, lead.Id);
    }

    private static string Str(JsonElement e, string k, int n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) ? Clean(v.ToString(), n) : "";
}
