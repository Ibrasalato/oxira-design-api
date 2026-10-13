using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Oxira.Design.Application.Common;
using Oxira.Design.Application.Emails;
using Oxira.Design.Application.Features.Orders;
using Oxira.Design.Domain;
using static Oxira.Design.Domain.Text;

namespace Oxira.Design.Application.Features.Account;

// ───── views (field names the account page reads) ─────
public record ProjectItem(string Pid, string Title, string Kind, DateTimeOffset Updated, string Share, bool Public);

public record OrderView(int Id, string Package, string Status, DateTimeOffset Created,
    [property: JsonPropertyName("price_note")] string PriceNote, double Area, string Style, string City, string Files,
    [property: JsonPropertyName("delivered_at")] DateTimeOffset? DeliveredAt, int Rating, string Review,
    [property: JsonPropertyName("has_designer")] bool HasDesigner, string Share, List<CommentDto> Comments,
    string? Notes = null, string? Client = null, string? Lang = null, string? Designer = null, List<string>? PlanTypes = null);

public record AccountView(bool Success, string Email, string Designer, List<ProjectItem> Projects, List<OrderView> Orders,
    List<OrderView>? Open = null, List<OrderView>? Mine = null, string? Reason = null) : IReply;

public record ProjectData(string Pid, string Title, string Data, string Share);
public record ProjectReply(bool Success, ProjectData? Project, string? Reason = null) : IReply;
public record SavedReply(bool Success, string Pid, string Share, string? Reason = null) : IReply;
public record PublishReply(bool Success, bool Public, string? Reason = null) : IReply;

// ───── inputs ─────
public record SaveProjectInput(string? Pid, string? Title, string? Kind, string? Data, string? Lang = null);
public record PublishInput(bool Public);
public record CommentInput(string? Text);
public record RateInput(int Rating, string? Review);
public record ApplyInput(string? Name, string? Skills, string? Countries, string? Note);

// ───── messages ─────
public record AccountQuery : IQuery<AccountView>;
public record GetProjectQuery(string Pid) : IQuery<ProjectReply>;
public record SaveProjectCommand(SaveProjectInput Input) : ICommand<SavedReply>;
public record PublishProjectCommand(string Pid, bool Public) : ICommand<PublishReply>;
public record DeleteProjectCommand(string Pid) : ICommand<Reply>;
public record CommentCommand(int OrderId, string? Text) : ICommand<Reply>;
public record ClaimOrderCommand(int OrderId) : ICommand<Reply>;
public record RateOrderCommand(int OrderId, int Rating, string? Review) : ICommand<Reply>;
public record ApplyDesignerCommand(ApplyInput Input) : ICommand<Reply>;
public record DeliverCommand(int OrderId, string? Note, IReadOnlyList<UploadedFile> Files) : ICommand<Reply>;

/// <summary>
/// The signed-in Oxira account on the design site: saved plans, own orders (matched by email) with their message threads and
/// ratings, and the designer portal (open orders, claim, deliver). Every handler needs a valid JWT; there is no other identity.
/// </summary>
public class AccountHandlers(IAppDbContext db, ICurrentUser me, TimeProvider clock, IEmailSender email, IRealtime realtime, IFileStore files,
    IOptions<SiteOptions> site)
    : IRequestHandler<AccountQuery, AccountView>, IRequestHandler<GetProjectQuery, ProjectReply>, IRequestHandler<SaveProjectCommand, SavedReply>,
      IRequestHandler<PublishProjectCommand, PublishReply>, IRequestHandler<DeleteProjectCommand, Reply>, IRequestHandler<CommentCommand, Reply>,
      IRequestHandler<ClaimOrderCommand, Reply>, IRequestHandler<RateOrderCommand, Reply>, IRequestHandler<ApplyDesignerCommand, Reply>,
      IRequestHandler<DeliverCommand, Reply>
{
    public const int MaxProjects = 50;
    public const int MaxProjectChars = 400_000;
    public const long MaxDeliveryBytes = 20 * 1024 * 1024;
    public static readonly string[] DeliveryExtensions = [".pdf", ".dwg", ".dxf", ".png", ".jpg", ".jpeg", ".webp", ".zip", ".max", ".skp", ".rvt", ".ifc", ".glb"];

    private string Team => site.Value.TeamEmail;

    // ───── load ─────
    public async Task<AccountView> Handle(AccountQuery q, CancellationToken ct)
    {
        var mail = Access.Email(me);
        var status = await Access.DesignerStatusAsync(db, me, ct);
        var projects = await db.Projects.AsNoTracking().Where(p => p.Owner == mail).OrderByDescending(p => p.UpdatedAt)
            .Select(p => new ProjectItem(p.Pid, p.Title, p.Kind, p.UpdatedAt, p.ShareId, p.Public)).ToListAsync(ct);
        var mineAsClient = await db.Orders.AsNoTracking().Where(o => o.Email == mail).OrderByDescending(o => o.Id).ToListAsync(ct);

        List<Order> open = [], mine = [];
        if (Access.IsDesigner(status))
        {
            open = await db.Orders.AsNoTracking().Where(o => o.Designer == null && !OrderStatuses.Closed.Contains(o.Status))
                .OrderByDescending(o => o.Id).Take(40).ToListAsync(ct);
            mine = status == DesignerStatuses.Admin
                ? await db.Orders.AsNoTracking().Where(o => o.Designer != null).OrderByDescending(o => o.Id).Take(200).ToListAsync(ct)
                : await db.Orders.AsNoTracking().Where(o => o.Designer == mail).OrderByDescending(o => o.Id).ToListAsync(ct);
        }
        var ids = mineAsClient.Concat(open).Concat(mine).Select(o => o.Id).Distinct().ToList();
        var comments = (await db.OrderComments.AsNoTracking().Where(c => ids.Contains(c.OrderId)).OrderBy(c => c.Id).ToListAsync(ct))
            .ToLookup(c => c.OrderId);

        OrderView View(Order o, bool designerView)
        {
            var (share, planTypes) = FromSummary(o.Summary);
            var thread = comments[o.Id].Select(c => new CommentDto(c.Role, c.Text, c.CreatedAt, c.Author == mail)).ToList();
            var v = new OrderView(o.Id, o.Package, o.Status, o.CreatedAt, o.PriceNote, o.Area, o.Style, o.City, o.FileNames, o.DeliveredAt,
                o.Rating, o.Review, o.Designer != null, share, thread);
            return designerView ? v with { Notes = o.Notes, Client = Clip(o.ContactName, 60), Lang = o.Lang, Designer = o.Designer ?? "", PlanTypes = planTypes } : v;
        }

        var designer = Access.IsDesigner(status);
        return new AccountView(true, mail, status, projects, mineAsClient.Select(o => View(o, false)).ToList(),
            designer ? open.Select(o => View(o, true)).ToList() : null, designer ? mine.Select(o => View(o, true)).ToList() : null);
    }

    private static (string Share, List<string> PlanTypes) FromSummary(string? summary)
    {
        if (string.IsNullOrEmpty(summary)) return ("", []);
        try
        {
            using var d = JsonDocument.Parse(summary);
            var r = d.RootElement;
            var share = r.TryGetProperty("share", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
            var types = r.TryGetProperty("planTypes", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => Clip(x.GetString(), 20)).Take(12).ToList() : [];
            return (share, types);
        }
        catch (JsonException) { return ("", []); }
    }

    // ───── projects ─────
    private Task<Project?> MyProjectAsync(string pid, CancellationToken ct)
    {
        var mail = Access.Email(me);
        return db.Projects.FirstOrDefaultAsync(p => p.Pid == pid && p.Owner == mail, ct);
    }

    public async Task<ProjectReply> Handle(GetProjectQuery q, CancellationToken ct)
    {
        var p = await MyProjectAsync(q.Pid ?? "", ct) ?? throw DomainException.NotFound("المخطط غير موجود");
        return new ProjectReply(true, new ProjectData(p.Pid, p.Title, p.Data, p.ShareId));
    }

    public async Task<SavedReply> Handle(SaveProjectCommand cmd, CancellationToken ct)
    {
        var mail = Access.Email(me);
        var i = cmd.Input;
        var data = string.IsNullOrEmpty(i.Data) ? "{}" : i.Data;
        if (data.Length > MaxProjectChars) throw DomainException.TooBig("المخطط كبير جدًا");
        var now = clock.GetUtcNow();
        var p = string.IsNullOrEmpty(i.Pid) ? null : await MyProjectAsync(i.Pid, ct);
        if (p == null)
        {
            if (await db.Projects.CountAsync(x => x.Owner == mail, ct) >= MaxProjects) throw DomainException.Limit($"الحد {MaxProjects} مخططًا لكل حساب");
            p = new Project { Pid = Ids.New(14), ShareId = Ids.New(16), Owner = mail, CreatedAt = now };
            db.Projects.Add(p);
        }
        p.Title = Clean(i.Title, 80) is { Length: > 0 } t ? t : "Plan";
        p.Kind = Clean(i.Kind, 20) is { Length: > 0 } k ? k : "plan";
        p.Data = data;
        if (!string.IsNullOrEmpty(i.Lang)) p.Lang = Langs.Of(i.Lang);
        p.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return new SavedReply(true, p.Pid, p.ShareId);
    }

    public async Task<PublishReply> Handle(PublishProjectCommand cmd, CancellationToken ct)
    {
        var p = await MyProjectAsync(cmd.Pid ?? "", ct) ?? throw DomainException.NotFound("المخطط غير موجود");
        p.Public = cmd.Public;
        p.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        var title = Clip(p.Title, 80);
        email.Enqueue(new EmailMessage([Team], (cmd.Public ? "مخطط جديد في المعرض: " : "أزيل من المعرض: ") + title + " | Oxira Design",
            Templates.TeamShell(cmd.Public ? "مخطط جديد في المعرض العام" : "مخطط أزيل من المعرض",
                $"<p>{Html(p.Owner)} {(cmd.Public ? "نشر" : "أزال")} المخطط <b>{Html(title)}</b> {(cmd.Public ? "في" : "من")} المعرض العام.<br>" +
                $"<a href=\"{site.Value.Site("/plan/?s=" + p.ShareId)}\">افتح المخطط</a><br>لإخفائه: غيّر Public إلى 0 في جدول Projects.</p>")));
        return new PublishReply(true, p.Public);
    }

    public async Task<Reply> Handle(DeleteProjectCommand cmd, CancellationToken ct)
    {
        var p = await MyProjectAsync(cmd.Pid ?? "", ct) ?? throw DomainException.NotFound("المخطط غير موجود");
        db.Projects.Remove(p);
        await db.SaveChangesAsync(ct);
        return Reply.Ok;
    }

    // ───── orders ─────
    /// <summary>the order and the caller's side of it: client (own email), designer (claimed it) or admin</summary>
    private async Task<(Order Order, string Role, string Status)> OrderForMeAsync(int id, CancellationToken ct)
    {
        var mail = Access.Email(me);
        var status = await Access.DesignerStatusAsync(db, me, ct);
        var o = await db.Orders.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw DomainException.NotFound("الطلب غير موجود");
        if (o.Email == mail) return (o, "client", status);
        if (status == DesignerStatuses.Admin || (status == DesignerStatuses.Active && o.Designer == mail)) return (o, "designer", status);
        throw DomainException.NotFound("الطلب غير موجود");
    }

    public async Task<Reply> Handle(CommentCommand cmd, CancellationToken ct)
    {
        var text = Clean(cmd.Text, 2000);
        if (text == "") throw DomainException.Invalid("اكتب رسالتك");
        var (o, role, _) = await OrderForMeAsync(cmd.OrderId, ct);
        var now = clock.GetUtcNow();
        var c = new OrderComment { OrderId = o.Id, Author = me.Email!, Role = role, Text = text, CreatedAt = now };
        db.OrderComments.Add(c);
        o.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        // email the other side: the client's message goes to the designer (or the team), the designer's to the client
        var to = role == "client" ? o.Designer ?? Team : o.Email;
        if (IsEmail(to))
        {
            var ar = o.Lang == "ar" || role == "client";
            var link = site.Value.Site((role == "designer" ? Langs.Prefix(o.Lang) : "") + "/account/");
            var inner = Templates.P((ar ? "رسالة جديدة على الطلب #" : "New message on order #") + o.Id + ":") + Templates.Quote(text) +
                $"<p><a href=\"{link}\">{(ar ? "افتح حسابك للرد" : "Open your account to reply")}</a></p>";
            email.Enqueue(new EmailMessage([to], (ar ? "رسالة جديدة على الطلب #" : "New message on order #") + o.Id + " | Oxira Design", Templates.Shell(ar, inner, 560)));
        }
        await realtime.OrderAsync(new OrderEvent(o.Id, "comment", o.Status, new CommentDto(c.Role, c.Text, c.CreatedAt, false)));
        return Reply.Ok;
    }

    public async Task<Reply> Handle(ClaimOrderCommand cmd, CancellationToken ct)
    {
        var mail = Access.Email(me);
        var status = await Access.DesignerStatusAsync(db, me, ct);
        if (!Access.IsDesigner(status)) throw DomainException.NotAvailable();
        var now = clock.GetUtcNow();
        // first designer wins: the row changes only while nobody has it
        var changed = await db.Orders.Where(o => o.Id == cmd.OrderId && o.Designer == null && !OrderStatuses.Closed.Contains(o.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Designer, mail).SetProperty(o => o.Status, OrderStatuses.Progress).SetProperty(o => o.UpdatedAt, now), ct);
        if (changed == 0) throw DomainException.NotAvailable("الطلب غير متاح أو استلمه مصمم آخر");
        var o = await db.Orders.AsNoTracking().FirstAsync(x => x.Id == cmd.OrderId, ct);
        email.Enqueue(new EmailMessage([Team], $"مصمم استلم الطلب #{o.Id} | Oxira Design",
            Templates.TeamShell("مصمم استلم طلبًا", $"<p>المصمم <b>{Html(mail)}</b> استلم الطلب #{o.Id} ({Html(o.Package)}). ملفات العميل في إيميل الطلب وفي مجلد الطلب على الخادم.</p>")));
        await realtime.OrderAsync(new OrderEvent(o.Id, "status", o.Status));
        return Reply.Ok;
    }

    public async Task<Reply> Handle(RateOrderCommand cmd, CancellationToken ct)
    {
        var mail = Access.Email(me);
        var o = await db.Orders.FirstOrDefaultAsync(x => x.Id == cmd.OrderId && x.Email == mail, ct) ?? throw DomainException.NotFound("الطلب غير موجود");
        if (o.DeliveredAt == null) throw DomainException.NotAvailable("التقييم بعد التسليم");
        if (cmd.Rating is < 1 or > 5) throw DomainException.Invalid("التقييم من 1 إلى 5");
        o.Rating = cmd.Rating;
        o.Review = Clean(cmd.Review, 1000);
        o.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Reply.Ok;
    }

    public async Task<Reply> Handle(ApplyDesignerCommand cmd, CancellationToken ct)
    {
        var mail = Access.Email(me);
        var i = cmd.Input;
        if (await db.Designers.AnyAsync(d => d.Email == mail, ct)) throw DomainException.Exists("طلبك مسجل مسبقًا");
        var name = Clean(i.Name, 80);
        var skills = Clean(i.Skills, 300);
        if (name == "" || skills == "") throw DomainException.Invalid("أدخل الاسم والمهارات");
        var now = clock.GetUtcNow();
        var d = new Designer { Email = mail, Name = name, Skills = skills, Countries = Clean(i.Countries, 120), Note = Clean(i.Note, 1000), CreatedAt = now, UpdatedAt = now };
        db.Designers.Add(d);
        await db.SaveChangesAsync(ct);
        email.Enqueue(new EmailMessage([Team], $"طلب انضمام مصمم: {name} | Oxira Design",
            Templates.TeamShell("طلب انضمام مصمم", $"<p><b>{Html(name)}</b> &lt;{Html(mail)}&gt;<br>المهارات: {Html(d.Skills)}<br>الدول: {Html(d.Countries)}<br>{Html(d.Note)}</p>" +
                "<p>للموافقة: غيّر Status إلى active في جدول Designers.</p>"), mail));
        return Reply.Ok;
    }

    public async Task<Reply> Handle(DeliverCommand cmd, CancellationToken ct)
    {
        var (o, role, status) = await OrderForMeAsync(cmd.OrderId, ct);
        var mail = me.Email!;
        if (role != "designer" || !(status == DesignerStatuses.Admin || o.Designer == mail)) throw DomainException.Denied("التسليم لمصمم الطلب فقط");
        if (!IsEmail(o.Email)) throw DomainException.Denied("لا يوجد بريد للعميل");
        var kept = cmd.Files.Where(f => DeliveryExtensions.Contains(Path.GetExtension(f.Name).ToLowerInvariant()) && f.Content.Length > 0).ToList();
        if (kept.Count == 0) throw DomainException.Invalid("أرفق ملفات التسليم");
        if (kept.Sum(f => (long)f.Content.Length) > MaxDeliveryBytes) throw DomainException.TooBig("حجم الملفات كبير جدًا (الحد 20 ميجابايت)");

        var now = clock.GetUtcNow();
        var attachments = new List<EmailAttachment>();
        var names = new List<string>();
        foreach (var f in kept)
        {
            var name = OrderHandlers.SafeName(f.Name);
            var path = await files.SaveAsync($"orders/{o.Id}/delivery", name, f.Content, ct);
            db.OrderFiles.Add(new OrderFile { OrderId = o.Id, Name = name, Mime = OrderHandlers.Mime(name), Role = "delivery", Field = Clip(f.Field, 40), Size = f.Content.Length, Path = path, CreatedAt = now });
            attachments.Add(new EmailAttachment(name, OrderHandlers.Mime(name), f.Content));
            names.Add(name);
        }
        var ar = o.Lang == "ar";
        var note = Clean(cmd.Note, 2000);
        o.Status = OrderStatuses.Delivered;
        o.DeliveredAt = now;
        o.UpdatedAt = now;
        db.OrderComments.Add(new OrderComment
        {
            OrderId = o.Id, Author = mail, Role = "designer", CreatedAt = now,
            Text = note != "" ? note : (ar ? "تم تسليم الملفات: " : "Files delivered: ") + string.Join(", ", names),
        });
        await db.SaveChangesAsync(ct);

        var link = site.Value.Site(Langs.Prefix(o.Lang) + "/account/");
        var inner = Templates.P((ar ? "تم تسليم ملفات طلبك #" : "The files for your order #") + o.Id + (ar ? "، تجدها مرفقة بهذه الرسالة." : " are attached to this email.")) +
            (note != "" ? Templates.Quote(note) : "") +
            $"<p><a href=\"{link}\">{(ar ? "قيّم التصميم أو اطلب تعديلاً من حسابك" : "Rate the work or ask for changes in your account")}</a></p>";
        email.Enqueue(new EmailMessage([o.Email], (ar ? "ملفات طلبك #" : "Your files, order #") + o.Id + " | Oxira Design", Templates.Shell(ar, inner, 600),
            "info@oxira.sa", attachments, [Team]));
        await realtime.OrderAsync(new OrderEvent(o.Id, "delivered", o.Status));
        return Reply.Ok;
    }
}
