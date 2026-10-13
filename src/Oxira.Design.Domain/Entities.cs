namespace Oxira.Design.Domain;

/// <summary>A request saved by the AI chat (create_lead), a studio finishing quote, or another form.</summary>
public class Lead
{
    public int Id { get; set; }
    public string ContactName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string City { get; set; } = "";
    /// <summary>studio | quick | design | premium | developer | finishing | not sure</summary>
    public string Package { get; set; } = "";
    public string Area { get; set; } = "";
    public string Needs { get; set; } = "";
    public string PreferredContact { get; set; } = "";
    /// <summary>website-chat | studio</summary>
    public string Channel { get; set; } = LeadChannels.Chat;
    public string Lang { get; set; } = Langs.Default;
    public string Page { get; set; } = "";
    /// <summary>the visitor's studio plan summary (chat) or quantities JSON (finishing)</summary>
    public string Context { get; set; } = "";
    public string Status { get; set; } = "new";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A design order from the order form, the studio or the plan designer. The id is the public order number.</summary>
public class Order
{
    public int Id { get; set; }
    /// <summary>free | quick | design | premium | developer | plan</summary>
    public string Package { get; set; } = Packages.Design;
    public string ContactName { get; set; } = "";
    public string Phone { get; set; } = "";
    /// <summary>lower case: the client's Oxira account is matched by this address</summary>
    public string Email { get; set; } = "";
    public string City { get; set; } = "";
    public double Area { get; set; }
    public string Style { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>names of the files the client sent, comma separated</summary>
    public string FileNames { get; set; } = "";
    /// <summary>studio / planner summary (JSON), when sent</summary>
    public string? Summary { get; set; }
    public string Rooms { get; set; } = "";
    public string PriceNote { get; set; } = "";
    /// <summary>SAR including VAT, paid online (quick package); 0 when quoted later</summary>
    public decimal AmountDue { get; set; }
    public string Status { get; set; } = OrderStatuses.New;
    public string Lang { get; set; } = Langs.Default;
    public string Page { get; set; } = "";
    public string ReturnUrl { get; set; } = "";
    public string? InvoiceId { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    /// <summary>email of the designer who took the order</summary>
    public string? Designer { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public int Rating { get; set; }
    public string Review { get; set; } = "";
    public bool ReviewAsked { get; set; }
    public bool NotifiedDesigners { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<OrderFile> Files { get; set; } = [];
}

/// <summary>A file kept on disk (Storage:Root): what the client sent with the order, or what the designer delivered.</summary>
public class OrderFile
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public string Name { get; set; } = "";
    public string Mime { get; set; } = "application/octet-stream";
    /// <summary>client | delivery</summary>
    public string Role { get; set; } = "client";
    /// <summary>form field it came in (plan0, source, preview, model …)</summary>
    public string Field { get; set; } = "";
    public long Size { get; set; }
    /// <summary>path relative to Storage:Root</summary>
    public string Path { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One message in an order's thread between the client and the designer (or the team).</summary>
public class OrderComment
{
    public long Id { get; set; }
    public int OrderId { get; set; }
    public string Author { get; set; } = "";
    /// <summary>client | designer</summary>
    public string Role { get; set; } = "client";
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A designer of the Oxira network, keyed by the email of their Oxira account.</summary>
public class Designer
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string Skills { get; set; } = "";
    public string Countries { get; set; } = "";
    public string Note { get; set; } = "";
    /// <summary>pending | active | admin</summary>
    public string Status { get; set; } = DesignerStatuses.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A plan saved from the plan designer, with a read-only share link and an optional place in the public gallery.</summary>
public class Project
{
    public int Id { get; set; }
    /// <summary>14 characters, used in /plan/?p=</summary>
    public string Pid { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "plan";
    public string Data { get; set; } = "{}";
    /// <summary>16 characters, used in /plan/?s=</summary>
    public string ShareId { get; set; } = "";
    public bool Public { get; set; }
    public string Lang { get; set; } = Langs.Default;
    /// <summary>the follow-up email about this plan was sent</summary>
    public bool Nudged { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Paid AI render credits, shared by the studio and the room redesign page. The id lives in the visitor's browser.</summary>
public class Wallet
{
    public string Id { get; set; } = "";
    public int Credits { get; set; }
    public int Purchased { get; set; }
    public int Used { get; set; }
    public string Email { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A render pack bought through Moyasar.</summary>
public class Purchase
{
    public int Id { get; set; }
    public string WalletId { get; set; } = "";
    public string Pack { get; set; } = "";
    public int Renders { get; set; }
    /// <summary>SAR including VAT</summary>
    public decimal Amount { get; set; }
    public string Email { get; set; } = "";
    public string Lang { get; set; } = Langs.Default;
    /// <summary>pending | paid</summary>
    public string Status { get; set; } = "pending";
    public string? InvoiceId { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One AI render attempt (studio view or room photo).</summary>
public class RenderLog
{
    public long Id { get; set; }
    /// <summary>studio | room</summary>
    public string Kind { get; set; } = "studio";
    public string SessionId { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Style { get; set; } = "";
    /// <summary>studio: top | orbit | walk; room: living, bedroom …</summary>
    public string View { get; set; } = "";
    /// <summary>free | paid</summary>
    public string Mode { get; set; } = "free";
    public string Lang { get; set; } = Langs.Default;
    public bool Ok { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A 3D listing tour (design.oxira.sa/tour/?id=). Draft until the 49 SAR invoice is paid.</summary>
public class Listing
{
    /// <summary>10 characters</summary>
    public string Id { get; set; } = "";
    /// <summary>draft | active</summary>
    public string Status { get; set; } = "draft";
    public string Title { get; set; } = "";
    public string City { get; set; } = "";
    public string District { get; set; } = "";
    public string Price { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Lang { get; set; } = Langs.Default;
    public string Style { get; set; } = "modern";
    public double Area { get; set; }
    public string Plan { get; set; } = "{}";
    /// <summary>{ar:{headline,body}, en:{headline,body}, hashtags:[]} written by Claude</summary>
    public string Copy { get; set; } = "{}";
    public int Views { get; set; }
    public string? InvoiceId { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A contractor-quote request from the build-cost calculator (/cost/).</summary>
public class BuildLead
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Country { get; set; } = "SA";
    public string City { get; set; } = "";
    public int Area { get; set; }
    public string Level { get; set; } = "";
    public string Soil { get; set; } = "";
    public long Low { get; set; }
    public long High { get; set; }
    public string Currency { get; set; } = "SAR";
    public string Plan { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Lang { get; set; } = Langs.Default;
    public string Page { get; set; } = "";
    public string Status { get; set; } = "new";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Build-cost rates for the calculator. The newest active row is served; the monthly research adds a pending row to approve.</summary>
public class CostRate
{
    public int Id { get; set; }
    public string Rates { get; set; } = "{}";
    /// <summary>active | pending</summary>
    public string Status { get; set; } = "pending";
    /// <summary>yyyy-MM</summary>
    public string Updated { get; set; } = "";
    /// <summary>approval link token (32 hex), cleared once used</summary>
    public string Token { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Sources { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Someone who asked to hear when Oxira Design Pro opens.</summary>
public class WaitlistEntry
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string Plan { get; set; } = "pro";
    public string Role { get; set; } = "";
    public string Lang { get; set; } = "";
    public string Country { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An address that asked not to get follow-up emails (orders still email as usual).</summary>
public class OptOut
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string Reason { get; set; } = "link";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Chat memory: the visible turns of one website chat session.</summary>
public class ChatMessage
{
    public long Id { get; set; }
    public string SessionId { get; set; } = "";
    /// <summary>user | assistant</summary>
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string Lang { get; set; } = Langs.Default;
    public string Page { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Daily usage counters (quotas). Key + Riyadh day.</summary>
public class UsageCounter
{
    public string Key { get; set; } = "";
    public DateOnly Day { get; set; }
    public int Count { get; set; }
}

/// <summary>A scheduled job period that already ran (daily:2026-10-13, weekly:…, monthly:2026-11), so it runs once even with several instances.</summary>
public class JobRun
{
    public string Key { get; set; } = "";
    public DateTimeOffset RanAt { get; set; }
}
