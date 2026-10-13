using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Oxira.Design.Domain;

namespace Oxira.Design.Application.Common;

/// <summary>The database as the application sees it (EF Core + SQL Server in Infrastructure).</summary>
public interface IAppDbContext
{
    DbSet<Lead> Leads { get; }
    DbSet<Order> Orders { get; }
    DbSet<OrderFile> OrderFiles { get; }
    DbSet<OrderComment> OrderComments { get; }
    DbSet<Designer> Designers { get; }
    DbSet<Project> Projects { get; }
    DbSet<Wallet> Wallets { get; }
    DbSet<Purchase> Purchases { get; }
    DbSet<RenderLog> RenderLogs { get; }
    DbSet<Listing> Listings { get; }
    DbSet<BuildLead> BuildLeads { get; }
    DbSet<CostRate> CostRates { get; }
    DbSet<WaitlistEntry> Waitlist { get; }
    DbSet<OptOut> OptOuts { get; }
    DbSet<ChatMessage> ChatMessages { get; }
    DbSet<JobRun> JobRuns { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>Atomic daily counters (SQL MERGE with HOLDLOCK), keyed by name and Riyadh day.</summary>
public interface IQuota
{
    /// <returns>the count after this use, or null when the limit was already reached</returns>
    Task<int?> TryConsumeAsync(string key, int limit, CancellationToken ct = default);
    Task RefundAsync(string key, CancellationToken ct = default);
    Task<int> UsedAsync(string key, CancellationToken ct = default);
}

/// <summary>The caller: the Oxira account from the JWT (issued by oxira-web-api) and the network address.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    string? Id { get; }
    /// <summary>lower case; orders, projects and designer status are matched by it</summary>
    string? Email { get; }
    string? Name { get; }
    /// <summary>Oxira staff and admins count as design admins</summary>
    bool IsStaff { get; }
    string Ip { get; }
}

// ───── email ─────
public record EmailAttachment(string Name, string Mime, byte[] Content, string? ContentId = null);
public record EmailMessage(IReadOnlyList<string> To, string Subject, string Html, string? ReplyTo = null,
    IReadOnlyList<EmailAttachment>? Attachments = null, IReadOnlyList<string>? Bcc = null);

/// <summary>Queues an email; delivery happens in the background (SMTP, Zoho Mail).</summary>
public interface IEmailSender
{
    void Enqueue(EmailMessage message);
}

// ───── AI ─────
/// <summary>One call to Anthropic's Messages API. Content and tools are raw JSON; Extra is merged into the body (e.g. thinking).</summary>
public record AiRequest(string System, JsonArray Messages, JsonArray? Tools = null, int MaxTokens = 900, string? Model = null, JsonObject? Extra = null);
public record AiResponse(JsonArray Content, string StopReason)
{
    /// <summary>all text blocks joined</summary>
    public string Text => string.Concat(Content.OfType<JsonObject>().Where(b => (string?)b["type"] == "text").Select(b => (string?)b["text"]));
}

public interface IAnthropic
{
    Task<AiResponse> CreateMessageAsync(AiRequest request, CancellationToken ct = default);
}

public record ImageInput(byte[] Data, string Mime);

/// <summary>An OpenAI image edit. Images: the view or photo first, optional style reference second.</summary>
public record ImageEdit(IReadOnlyList<ImageInput> Images, string Prompt, string Model, string Size, string Quality, int Compression, string? InputFidelity = null);

public interface IImageEditor
{
    /// <returns>the JPEG bytes</returns>
    Task<byte[]> EditAsync(ImageEdit request, CancellationToken ct = default);
}

// ───── payments (Moyasar invoices) ─────
/// <param name="Amount">halalas</param>
public record InvoiceRequest(int Amount, string Description, string SuccessUrl, string BackUrl, IReadOnlyDictionary<string, string> Metadata);
/// <param name="Amount">halalas</param>
public record Invoice(string Id, string Status, int Amount, string? Url, IReadOnlyDictionary<string, string> Metadata);

public interface IPayments
{
    Task<Invoice> CreateInvoiceAsync(InvoiceRequest request, CancellationToken ct = default);
    /// <returns>null when Moyasar does not know the id</returns>
    Task<Invoice?> GetInvoiceAsync(string id, CancellationToken ct = default);
}

// ───── files ─────
/// <summary>Order files on disk under Storage:Root (paths are relative to it).</summary>
public interface IFileStore
{
    Task<string> SaveAsync(string folder, string name, byte[] content, CancellationToken ct = default);
    Task<byte[]> ReadAsync(string path, CancellationToken ct = default);
    Stream OpenRead(string path);
}

// ───── live updates (SignalR /hubs/design) ─────
public record WalletEvent(string Wallet, int Credits);
/// <summary>kind: comment | status | delivered | paid</summary>
public record OrderEvent(int OrderId, string Kind, string Status, CommentDto? Comment = null);

public interface IRealtime
{
    Task WalletAsync(WalletEvent e);
    Task OrderAsync(OrderEvent e);
}
