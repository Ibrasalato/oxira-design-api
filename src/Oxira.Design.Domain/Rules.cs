using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Oxira.Design.Domain;

/// <summary>A rule was broken. Reason is the short code the website switches on (invalid, limit, not_found …).</summary>
public class DomainException(string message, string reason = "invalid", int status = 400) : Exception(message)
{
    public string Reason { get; } = reason;
    public int Status { get; } = status;

    public static DomainException Invalid(string message = "بيانات غير صالحة") => new(message, "invalid", 400);
    public static DomainException NotFound(string message = "غير موجود") => new(message, "not_found", 404);
    public static DomainException Limit(string message = "وصلت للحد اليومي") => new(message, "limit", 429);
    public static DomainException TooBig(string message = "الحجم كبير جدًا") => new(message, "too_big", 413);
    public static DomainException NotAvailable(string message = "الطلب غير متاح") => new(message, "not_available", 409);
    public static DomainException Exists(string message = "مسجل مسبقًا") => new(message, "exists", 409);
    public static DomainException Denied(string message = "غير مسموح") => new(message, "denied", 403);
    public static DomainException Session() => new("سجّل الدخول بحساب أوكسيرا", "session", 401);
}

public static class Langs
{
    public const string Default = "ar";
    /// <summary>the languages of design.oxira.sa</summary>
    public static readonly string[] All = ["ar", "en", "de", "fr", "ru", "es", "tr", "zh", "hi", "ur"];
    /// <summary>languages with their own email texts (others get English)</summary>
    public static readonly string[] Email = ["ar", "en", "de", "fr", "ru"];
    public static string Of(string? lang) => lang != null && All.Contains(lang) ? lang : Default;
    /// <summary>the site's path prefix for a language ("" for Arabic, "/en" …)</summary>
    public static string Prefix(string? lang) => Of(lang) == Default ? "" : "/" + Of(lang);
}

public static class LeadChannels
{
    public const string Chat = "website-chat";
    public const string Studio = "studio";
}

public static class Packages
{
    public const string Free = "free", Quick = "quick", Design = "design", Premium = "premium", Developer = "developer", Plan = "plan";
    public static readonly string[] All = [Free, Quick, Design, Premium, Developer, Plan];
    public static string Of(string? p) => p != null && All.Contains(p) ? p : Design;

    public static string ArabicName(string p) => p switch
    {
        Quick => "سريعة", Design => "تصميم داخلي", Premium => "متكاملة", Developer => "مطورين", Plan => "تصميم مخطط", Free => "الاستوديو المجاني", _ => p,
    };
}

/// <summary>
/// Order statuses as stored and returned by the API. The website shows them through STATUS_KEYS (src/i18n/account.ts);
/// the Arabic names are what the team sees in emails.
/// </summary>
public static class OrderStatuses
{
    public const string New = "new", Unpaid = "unpaid", Paid = "paid", Progress = "progress", Delivered = "delivered", Cancelled = "cancelled";
    public static readonly string[] All = [New, Unpaid, Paid, Progress, Delivered, Cancelled];
    /// <summary>a designer can no longer take these</summary>
    public static readonly string[] Closed = [Unpaid, Delivered, Cancelled];

    public static string Arabic(string s) => s switch
    {
        New => "جديد", Unpaid => "بانتظار الدفع", Paid => "مدفوع", Progress => "قيد التنفيذ", Delivered => "تم التسليم", Cancelled => "ملغي", _ => s,
    };
}

public static class DesignerStatuses
{
    public const string Pending = "pending", Active = "active", Admin = "admin";
}

/// <summary>Prices in SAR before VAT unless noted. Keep in sync with PRICES (src/i18n/content.ts) and REDESIGN_PACKS (src/i18n/redesign.ts).</summary>
public static class Prices
{
    public const decimal Quick = 249m;
    public const decimal DesignPerM2 = 35m;
    public const decimal DesignMin = 2500m;
    public const decimal Developer = 4900m;
    public const decimal Vat = 0.15m;
    /// <summary>listing tour, SAR including VAT</summary>
    public const decimal Listing = 49m;

    public static decimal WithVat(decimal net) => Math.Round(net * (1 + Vat), 2, MidpointRounding.AwayFromZero);
    public static int Halalas(decimal sar) => (int)Math.Round(sar * 100m, MidpointRounding.AwayFromZero);

    public record Pack(string Id, int Renders, decimal Price);
    /// <summary>render packs, SAR including VAT</summary>
    public static readonly Pack[] Packs = [new("p10", 10, 29m), new("p30", 30, 69m), new("p100", 100, 179m)];
}

/// <summary>Saudi Arabia keeps UTC+3 all year (no daylight saving).</summary>
public static class Riyadh
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(3);
    public static DateTimeOffset Now(TimeProvider clock) => clock.GetUtcNow().ToOffset(Offset);
    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(Now(clock).DateTime);
}

public static partial class Text
{
    /// <summary>trim, drop control characters, cut to max</summary>
    public static string Clip(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = Controls().Replace(s.Trim(), " ");
        return t.Length <= max ? t : t[..max].TrimEnd();
    }

    /// <summary>Clip and drop angle brackets (fields shown in emails and on the site).</summary>
    public static string Clean(string? s, int max) => Clip((s ?? "").Replace("<", "").Replace(">", ""), max);

    public static bool IsEmail(string? s) => !string.IsNullOrWhiteSpace(s) && s.Length <= 160 && EmailRe().IsMatch(s.Trim());

    /// <summary>a valid address in lower case, or ""</summary>
    public static string Email(string? s) => IsEmail(s) ? s!.Trim().ToLowerInvariant() : "";

    public static string Digits(string? s) => new((s ?? "").Where(char.IsAsciiDigit).ToArray());

    public static bool IsPhone(string? s) => Digits(s).Length is >= 7 and <= 15;

    /// <summary>session ids from the browser: letters, digits, _ and -, at most 64</summary>
    public static string SessionKey(string? sid) => Clip(SafeId().Replace(sid ?? "", ""), 64);

    public static bool IsWallet(string? w) => w != null && WalletRe().IsMatch(w);

    public static string Html(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "").Replace("\n", "<br>");

    /// <summary>JSON object text inside a model answer (from the first { to the last })</summary>
    public static string? JsonObjectIn(string text)
    {
        var a = text.IndexOf('{');
        var z = text.LastIndexOf('}');
        return a >= 0 && z > a ? text[a..(z + 1)] : null;
    }

    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F]")] private static partial Regex Controls();
    [GeneratedRegex(@"^[^\s@<>""'(),;:\\]{1,64}@[^\s@<>""'(),;:\\]{1,120}\.[^\s@<>""'(),;:\\]{2,24}$")] private static partial Regex EmailRe();
    [GeneratedRegex(@"[^A-Za-z0-9_-]")] private static partial Regex SafeId();
    [GeneratedRegex(@"^[a-zA-Z0-9]{16,48}$")] private static partial Regex WalletRe();
}

/// <summary>Random public ids (no 0/1/l/o so they read well in links).</summary>
public static class Ids
{
    public const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    public static string New(int length)
    {
        Span<char> c = stackalloc char[length];
        for (var i = 0; i < length; i++) c[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(c);
    }

    public static string Hex(int length) => Convert.ToHexString(RandomNumberGenerator.GetBytes((length + 1) / 2)).ToLowerInvariant()[..length];

    public static bool Is(string? s, int length) => s != null && s.Length == length && s.All(ch => Alphabet.Contains(ch));
}
