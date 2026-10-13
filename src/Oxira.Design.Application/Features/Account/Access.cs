using MediatR;
using Microsoft.EntityFrameworkCore;
using Oxira.Design.Application.Common;
using Oxira.Design.Domain;

namespace Oxira.Design.Application.Features.Account;

/// <summary>Ids of the orders the signed-in user can see (as client, as their designer, or all claimed ones as admin). Only: check one order.</summary>
public record MyOrderIdsQuery(string Email, bool IsStaff, int? Only = null) : IQuery<List<int>>;

/// <summary>Who the caller is on the design site.</summary>
public static class Access
{
    /// <summary>"" (client only), pending, active or admin. Oxira staff and admins are design admins.</summary>
    public static Task<string> DesignerStatusAsync(IAppDbContext db, ICurrentUser me, CancellationToken ct) =>
        me.IsAuthenticated ? DesignerStatusAsync(db, me.Email!, me.IsStaff, ct) : Task.FromResult("");

    public static async Task<string> DesignerStatusAsync(IAppDbContext db, string email, bool isStaff, CancellationToken ct)
    {
        if (isStaff) return DesignerStatuses.Admin;
        return await db.Designers.AsNoTracking().Where(d => d.Email == email).Select(d => d.Status).FirstOrDefaultAsync(ct) ?? "";
    }

    public static bool IsDesigner(string status) => status is DesignerStatuses.Active or DesignerStatuses.Admin;

    /// <summary>the signed-in email, or a 401 "session" error</summary>
    public static string Email(ICurrentUser me) => me.IsAuthenticated ? me.Email! : throw DomainException.Session();
}

public class AccessHandlers(IAppDbContext db) : IRequestHandler<MyOrderIdsQuery, List<int>>
{
    public async Task<List<int>> Handle(MyOrderIdsQuery q, CancellationToken ct)
    {
        var email = q.Email.Trim().ToLowerInvariant();
        if (email == "") return [];
        var status = await Access.DesignerStatusAsync(db, email, q.IsStaff, ct);
        var orders = db.Orders.AsNoTracking();
        if (q.Only is { } only) orders = orders.Where(o => o.Id == only);
        orders = status switch
        {
            DesignerStatuses.Admin => orders.Where(o => o.Email == email || o.Designer != null),
            DesignerStatuses.Active => orders.Where(o => o.Email == email || o.Designer == email),
            _ => orders.Where(o => o.Email == email),
        };
        return await orders.OrderByDescending(o => o.Id).Select(o => o.Id).Take(500).ToListAsync(ct);
    }
}
