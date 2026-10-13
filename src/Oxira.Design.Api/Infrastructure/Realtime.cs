using MediatR;
using Microsoft.AspNetCore.SignalR;
using Oxira.Design.Application.Common;
using Oxira.Design.Application.Features.Account;
using Oxira.Design.Domain;

namespace Oxira.Design.Api.Infrastructure;

/// <summary>
/// /hubs/design. Events: "wallet" (WalletEvent) to a wallet's group after a purchase or render; "order" (OrderEvent) to the
/// people of an order (comment, status, delivered, paid); "paid" (OrderEvent) to anyone waiting on an order's payment.
/// Signed-in users (?access_token=) join the groups of their orders when they connect; anyone may join a wallet or a payment.
/// </summary>
public class DesignHub(ISender mediator) : Hub
{
    /// <summary>the account of this connection (hub calls read the user from the connection, not from an HTTP request)</summary>
    private (string Email, bool Staff)? Me()
    {
        var u = Context.User;
        var email = u?.FindFirst(OxiraClaims.Email)?.Value?.Trim().ToLowerInvariant();
        if (u?.Identity?.IsAuthenticated != true || string.IsNullOrEmpty(email)) return null;
        return (email, u.Claims.Any(c => c.Type == OxiraClaims.Role && OxiraClaims.StaffRoles.Contains(c.Value)));
    }

    public static string WalletGroup(string id) => "wallet:" + id;
    public static string OrderGroup(int id) => "order:" + id;
    public static string PayGroup(int id) => "pay:" + id;

    public override async Task OnConnectedAsync()
    {
        if (Me() is { } me)
            foreach (var id in await mediator.Send(new MyOrderIdsQuery(me.Email, me.Staff)))
                await Groups.AddToGroupAsync(Context.ConnectionId, OrderGroup(id));
        await base.OnConnectedAsync();
    }

    /// <summary>credits of this wallet (the id is the secret the browser holds)</summary>
    public Task JoinWallet(string wallet) => Text.IsWallet(wallet) ? Groups.AddToGroupAsync(Context.ConnectionId, WalletGroup(wallet)) : Task.CompletedTask;

    /// <summary>tells the page when this order's payment is confirmed (no order details are sent)</summary>
    public Task JoinPayment(int orderId) => Groups.AddToGroupAsync(Context.ConnectionId, PayGroup(orderId));

    /// <summary>an order the signed-in user can see (client, its designer, or an admin)</summary>
    public async Task<bool> JoinOrder(int orderId)
    {
        if (Me() is not { } me || !(await mediator.Send(new MyOrderIdsQuery(me.Email, me.Staff, orderId))).Contains(orderId)) return false;
        await Groups.AddToGroupAsync(Context.ConnectionId, OrderGroup(orderId));
        return true;
    }
}

public class SignalRRealtime(IHubContext<DesignHub> hub, ILogger<SignalRRealtime> log) : IRealtime
{
    public Task WalletAsync(WalletEvent e) => Send(DesignHub.WalletGroup(e.Wallet), "wallet", e);

    public async Task OrderAsync(OrderEvent e)
    {
        await Send(DesignHub.OrderGroup(e.OrderId), "order", e);
        if (e.Kind == "paid") await Send(DesignHub.PayGroup(e.OrderId), "paid", e with { Comment = null });
    }

    private async Task Send(string group, string evt, object payload)
    {
        try { await hub.Clients.Group(group).SendAsync(evt, payload); }
        catch (Exception e) { log.LogWarning(e, "Realtime {Event} failed", evt); }
    }
}
