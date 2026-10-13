using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Oxira.Design.Application.Common;
using Oxira.Design.Domain;
using static Oxira.Design.Domain.Text;

namespace Oxira.Design.Application.Features.Public;

public record SharedPlanReply(bool Success, string? Title, string? Data, DateTimeOffset? Updated, string? Reason = null) : IReply;
public record GalleryItem(string Title, string Share, string Data, DateTimeOffset Updated, string Lang);
public record DesignerCard(int Id, string Name, string Skills, string Countries, double Rating, int Reviews, List<string> Quotes);
public record ListReply<T>(bool Success, List<T> Items, string? Reason = null) : IReply;

public record WaitlistInput(string? Email, string? Plan, string? Role, string? Lang, string? Country,
    [property: System.Text.Json.Serialization.JsonPropertyName("company_website")] string? CompanyWebsite);

public record SharedPlanQuery(string S) : IQuery<SharedPlanReply>;
public record GalleryQuery : IQuery<ListReply<GalleryItem>>;
public record DesignersQuery : IQuery<ListReply<DesignerCard>>;
public record JoinWaitlistCommand(WaitlistInput Input) : ICommand<Reply>;

/// <summary>Public pages: shared plans (read-only links), the gallery of published plans, the designer directory (never emails) and the Pro waitlist.</summary>
public class PublicHandlers(IAppDbContext db, TimeProvider clock, IEmailSender email, IOptions<SiteOptions> site)
    : IRequestHandler<SharedPlanQuery, SharedPlanReply>, IRequestHandler<GalleryQuery, ListReply<GalleryItem>>,
      IRequestHandler<DesignersQuery, ListReply<DesignerCard>>, IRequestHandler<JoinWaitlistCommand, Reply>
{
    public async Task<SharedPlanReply> Handle(SharedPlanQuery q, CancellationToken ct)
    {
        if (!Ids.Is(q.S, 16)) return new SharedPlanReply(false, null, null, null, "not_found");
        var p = await db.Projects.AsNoTracking().Where(x => x.ShareId == q.S).Select(x => new { x.Title, x.Data, x.UpdatedAt }).FirstOrDefaultAsync(ct);
        return p == null ? new SharedPlanReply(false, null, null, null, "not_found") : new SharedPlanReply(true, p.Title, p.Data, p.UpdatedAt);
    }

    public async Task<ListReply<GalleryItem>> Handle(GalleryQuery q, CancellationToken ct) =>
        new(true, await db.Projects.AsNoTracking().Where(p => p.Public).OrderByDescending(p => p.UpdatedAt).Take(36)
            .Select(p => new GalleryItem(p.Title, p.ShareId, p.Data, p.UpdatedAt, p.Lang)).ToListAsync(ct));

    public async Task<ListReply<DesignerCard>> Handle(DesignersQuery q, CancellationToken ct)
    {
        var designers = await db.Designers.AsNoTracking().Where(d => d.Status == DesignerStatuses.Active).ToListAsync(ct);
        var emails = designers.Select(d => d.Email).ToList();
        var rated = await db.Orders.AsNoTracking().Where(o => o.Rating > 0 && o.Designer != null && emails.Contains(o.Designer))
            .OrderBy(o => o.Id).Select(o => new { o.Designer, o.Rating, o.Review }).ToListAsync(ct);
        var items = designers.Select(d =>
        {
            var mine = rated.Where(o => o.Designer == d.Email).ToList();
            var avg = mine.Count > 0 ? Math.Round(mine.Average(o => o.Rating), 1) : 0;
            return new DesignerCard(d.Id, Clip(d.Name, 60), d.Skills, d.Countries, avg, mine.Count,
                mine.Where(o => o.Review != "").TakeLast(3).Select(o => Clip(o.Review, 240)).ToList());
        }).Where(d => d.Name != "").OrderByDescending(d => d.Rating).ThenByDescending(d => d.Reviews).ToList();
        return new ListReply<DesignerCard>(true, items);
    }

    public async Task<Reply> Handle(JoinWaitlistCommand cmd, CancellationToken ct)
    {
        var i = cmd.Input;
        if (!string.IsNullOrEmpty(i.CompanyWebsite)) return Reply.Ok; // honeypot
        var mail = Email(i.Email);
        if (mail == "") return new Reply(false, "email");
        var now = clock.GetUtcNow();
        var row = await db.Waitlist.FirstOrDefaultAsync(w => w.Email == mail, ct);
        if (row == null) db.Waitlist.Add(row = new WaitlistEntry { Email = mail, CreatedAt = now });
        row.Plan = Clean(i.Plan, 20) is { Length: > 0 } p ? p : "pro";
        row.Role = Clean(i.Role, 40);
        row.Lang = Clean(i.Lang, 5);
        row.Country = Clean(i.Country, 2).ToUpperInvariant();
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        email.Enqueue(new EmailMessage([site.Value.TeamEmail], "Pro waitlist: " + mail,
            $"<p>New Pro waitlist sign-up: {Html(mail)} · {Html(row.Role)} · {Html(row.Country)} · {Html(row.Lang)}</p>"));
        return Reply.Ok;
    }
}
