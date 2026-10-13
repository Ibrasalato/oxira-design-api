using System.Text.Json;
using System.Text.Json.Nodes;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oxira.Design.Application.Common;
using Oxira.Design.Application.Emails;
using Oxira.Design.Domain;
using static Oxira.Design.Domain.Text;

namespace Oxira.Design.Application.Features.Chat;

/// <summary>context: the visitor's studio plan summary (≤ 3000 characters)</summary>
public record ChatInput(string? SessionId, string? Message, string? Lang, string? Page, string? Context);
public record ChatReply(bool Success, string Reply, string? Reason = null) : IReply;
public record ChatTurnDto(string Role, string Content, DateTimeOffset At);

public record SendChatCommand(ChatInput Input) : ICommand<ChatReply>;
public record GetChatQuery(string SessionId) : IQuery<List<ChatTurnDto>>;

/// <summary>
/// The website assistant: Claude with one tool (create_lead). Memory is the last visible turns of the session.
/// When it saves a lead the server emails the team (not the model), so the email is always complete.
/// </summary>
public class ChatHandlers(IAppDbContext db, TimeProvider clock, IAnthropic ai, IQuota quota, IEmailSender email,
    IOptions<AiOptions> opt, IOptions<SiteOptions> site, ILogger<ChatHandlers> log)
    : IRequestHandler<SendChatCommand, ChatReply>, IRequestHandler<GetChatQuery, List<ChatTurnDto>>
{
    public const string Sorry = "المعذرة، صار خلل بسيط. جرّب مرة ثانية أو كلمنا على واتساب +966 59 669 4021.";
    public const string DailyLimit = "وصلت للحد اليومي للمحادثة. راسلنا على واتساب +966 59 669 4021 ونخدمك مباشرة.";

    private static JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };

    private static JsonArray Tools() =>
    [
        new JsonObject
        {
            ["name"] = "create_lead",
            ["description"] = "Save a new request from an Oxira Design website visitor. Call ONCE per request, only after the visitor confirmed the summary. The returned id is the request number to give the visitor; the team is notified automatically.",
            ["input_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["contact_name"] = Str("Visitor full name as they gave it"),
                    ["phone"] = Str("Visitor mobile number with country code, digits only"),
                    ["email"] = Str("Visitor email, or empty string"),
                    ["city"] = Str("City and country, or empty string"),
                    ["package"] = Str("Package of interest: studio, quick, design, premium, developer, or not sure"),
                    ["area"] = Str("Approximate area in square metres, or empty string"),
                    ["needs"] = Str("Arabic summary of what the visitor needs, including property type and file type"),
                    ["preferred_contact"] = Str("How and when the visitor prefers to be contacted, or empty string"),
                },
                ["required"] = new JsonArray("contact_name", "phone", "needs"),
            },
        },
    ];

    public async Task<ChatReply> Handle(SendChatCommand cmd, CancellationToken ct)
    {
        var i = cmd.Input;
        var sid = SessionKey(i.SessionId);
        var text = Clip(i.Message, 2000);
        if (sid.Length < 6 || text == "") throw new DomainException("missing message", "missing");
        var lang = Langs.Of(i.Lang);
        var page = Clip(i.Page, 300);
        var context = Clip((i.Context ?? "").Replace("{", "").Replace("}", ""), 3000);

        if (await quota.TryConsumeAsync($"chat:s:{sid}", opt.Value.MessagesPerSession, ct) == null ||
            await quota.TryConsumeAsync("chat:all", opt.Value.MessagesPerDay, ct) == null)
            return new ChatReply(false, DailyLimit, "limit");

        var history = await db.ChatMessages.AsNoTracking().Where(m => m.SessionId == sid)
            .OrderByDescending(m => m.Id).Take(opt.Value.Memory).ToListAsync(ct);
        history.Reverse();
        var messages = new JsonArray();
        foreach (var m in history.SkipWhile(m => m.Role != "user")) messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });

        db.ChatMessages.Add(new ChatMessage { SessionId = sid, Role = "user", Content = text, Lang = lang, Page = page, CreatedAt = clock.GetUtcNow() });

        string reply;
        var ok = true;
        try { reply = await RunAgentAsync(Prompts.System(Riyadh.Now(clock), lang, page, context), messages, lang, page, context, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Chat agent failed for {Session}", sid);
            reply = "";
        }
        reply = Clip(reply, 4000);
        if (reply == "") { reply = Sorry; ok = false; }
        if (ok) db.ChatMessages.Add(new ChatMessage { SessionId = sid, Role = "assistant", Content = reply, Lang = lang, Page = page, CreatedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct);
        return ok ? new ChatReply(true, reply) : new ChatReply(false, reply, "failed");
    }

    private async Task<string> RunAgentAsync(string system, JsonArray messages, string lang, string page, string context, CancellationToken ct)
    {
        var saved = false;
        for (var step = 0; step < opt.Value.MaxIterations; step++)
        {
            var res = await ai.CreateMessageAsync(new AiRequest(system, messages, Tools(), opt.Value.MaxTokens, opt.Value.Model), ct);
            var uses = res.Content.OfType<JsonObject>().Where(b => (string?)b["type"] == "tool_use").ToList();
            if (res.StopReason != "tool_use" || uses.Count == 0) return res.Text.Trim();

            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = res.Content.DeepClone() });
            var results = new JsonArray();
            foreach (var u in uses)
            {
                string output;
                if ((string?)u["name"] == "create_lead" && !saved)
                {
                    var id = await SaveLeadAsync(u["input"] as JsonObject ?? [], lang, page, context, ct);
                    saved = true;
                    output = JsonSerializer.Serialize(new { success = true, id });
                }
                else output = JsonSerializer.Serialize(new { success = false, error = saved ? "already saved in this reply" : "unknown tool" });
                results.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = (string?)u["id"], ["content"] = output });
            }
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
        }
        return "";
    }

    private async Task<int> SaveLeadAsync(JsonObject input, string lang, string page, string context, CancellationToken ct)
    {
        string F(string k, int max) => Clip(input[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : input[k]?.ToJsonString(), max);
        var lead = new Lead
        {
            ContactName = F("contact_name", 120), Phone = F("phone", 40), Email = F("email", 160), City = F("city", 120),
            Package = F("package", 40), Area = F("area", 40), Needs = F("needs", 5000), PreferredContact = F("preferred_contact", 200),
            Channel = LeadChannels.Chat, Lang = lang, Page = page, Context = context, CreatedAt = clock.GetUtcNow(),
        };
        db.Leads.Add(lead);
        await db.SaveChangesAsync(ct);
        email.Enqueue(Templates.TeamChatLead(lead, site.Value.TeamEmail));
        return lead.Id;
    }

    public async Task<List<ChatTurnDto>> Handle(GetChatQuery q, CancellationToken ct)
    {
        var sid = SessionKey(q.SessionId);
        return await db.ChatMessages.AsNoTracking().Where(m => m.SessionId == sid).OrderBy(m => m.Id)
            .Select(m => new ChatTurnDto(m.Role, m.Content, m.CreatedAt)).ToListAsync(ct);
    }
}
