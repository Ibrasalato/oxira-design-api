using Microsoft.EntityFrameworkCore;
using Oxira.Design.Application.Common;
using Oxira.Design.Domain;

namespace Oxira.Design.Application.Features.Redesign;

/// <summary>Render credits. Every change is one conditional UPDATE, so two renders at once never spend the same credit; the new balance is pushed to the wallet's SignalR group.</summary>
public class Wallets(IAppDbContext db, TimeProvider clock, IRealtime realtime)
{
    public async Task<int> BalanceAsync(string wallet, CancellationToken ct) =>
        await db.Wallets.AsNoTracking().Where(w => w.Id == wallet).Select(w => (int?)w.Credits).FirstOrDefaultAsync(ct) ?? 0;

    /// <returns>credits left after spending one, or null when there was none</returns>
    public async Task<int?> SpendAsync(string wallet, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var n = await db.Wallets.Where(w => w.Id == wallet && w.Credits > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Credits, w => w.Credits - 1).SetProperty(w => w.Used, w => w.Used + 1).SetProperty(w => w.UpdatedAt, now), ct);
        if (n == 0) return null;
        return await PushAsync(wallet, ct);
    }

    /// <summary>gives back the credit of a render that failed</summary>
    public async Task<int> RefundAsync(string wallet, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.Wallets.Where(w => w.Id == wallet)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Credits, w => w.Credits + 1).SetProperty(w => w.Used, w => w.Used > 0 ? w.Used - 1 : 0).SetProperty(w => w.UpdatedAt, now), ct);
        return await PushAsync(wallet, ct);
    }

    /// <summary>adds a bought pack (creates the wallet on its first purchase)</summary>
    public async Task<int> CreditAsync(string wallet, int renders, string email, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var n = await db.Wallets.Where(w => w.Id == wallet)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Credits, w => w.Credits + renders).SetProperty(w => w.Purchased, w => w.Purchased + renders)
                .SetProperty(w => w.Email, w => email != "" ? email : w.Email).SetProperty(w => w.UpdatedAt, now), ct);
        if (n == 0)
        {
            db.Wallets.Add(new Wallet { Id = wallet, Credits = renders, Purchased = renders, Email = email, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync(ct);
        }
        return await PushAsync(wallet, ct);
    }

    private async Task<int> PushAsync(string wallet, CancellationToken ct)
    {
        var credits = await BalanceAsync(wallet, ct);
        await realtime.WalletAsync(new WalletEvent(wallet, credits));
        return credits;
    }
}
