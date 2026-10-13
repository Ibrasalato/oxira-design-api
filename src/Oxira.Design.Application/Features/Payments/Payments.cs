using MediatR;
using Microsoft.Extensions.Logging;
using Oxira.Design.Application.Common;
using Oxira.Design.Application.Features.Orders;

namespace Oxira.Design.Application.Features.Payments;

/// <summary>Moyasar called back about an invoice. The body is never trusted: the invoice is fetched again from Moyasar.</summary>
public record PaymentCallbackCommand(string? InvoiceId) : ICommand<bool>;

public class PaymentHandlers(IPayments payments, ISender mediator, ILogger<PaymentHandlers> log) : IRequestHandler<PaymentCallbackCommand, bool>
{
    public async Task<bool> Handle(PaymentCallbackCommand cmd, CancellationToken ct)
    {
        var id = (cmd.InvoiceId ?? "").Trim();
        if (id.Length is 0 or > 64) return false;
        var inv = await payments.GetInvoiceAsync(id, ct);
        if (inv == null || inv.Status != "paid") return false;
        var kind = inv.Metadata.GetValueOrDefault("kind");
        log.LogInformation("Moyasar invoice {Id} paid ({Kind}, {Amount} halalas)", inv.Id, kind, inv.Amount);
        return kind switch
        {
            PaymentKinds.Design => await mediator.Send(new MarkOrderPaidCommand(inv), ct),
            _ => false,
        };
    }
}
