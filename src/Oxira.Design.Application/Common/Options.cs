namespace Oxira.Design.Application.Common;

/// <summary>"Site" section.</summary>
public class SiteOptions
{
    /// <summary>team inbox for orders, leads and reports</summary>
    public string TeamEmail { get; set; } = "info@oxira.sa";
    public string SiteUrl { get; set; } = "https://design.oxira.sa";
    /// <summary>public address of this API (payment callback, approval and opt-out links)</summary>
    public string ApiUrl { get; set; } = "https://api.oxira.sa/design";
    public string WhatsApp { get; set; } = "+966 59 669 4021";
    /// <summary>return addresses accepted for payment redirects</summary>
    public string[] ReturnUrls { get; set; } = ["https://design.oxira.sa/", "https://ibrasalato.github.io/", "http://localhost:4321/"];

    public string Site(string path) => SiteUrl.TrimEnd('/') + path;
    public string Api(string path) => ApiUrl.TrimEnd('/') + path;

    /// <summary>the given address when it is on the site, otherwise the fallback (keeps payment redirects on our pages)</summary>
    public string SafeReturn(string? url, string fallback)
    {
        var u = (url ?? "").Trim();
        if (u.Length > 300) u = u[..300];
        return ReturnUrls.Any(a => u.StartsWith(a, StringComparison.Ordinal)) ? u : fallback;
    }
}

/// <summary>"Ai" section: the website chat agent.</summary>
public class AiOptions
{
    public string Model { get; set; } = "claude-sonnet-5";
    public int MaxTokens { get; set; } = 900;
    public int MaxIterations { get; set; } = 6;
    /// <summary>visible turns kept as memory</summary>
    public int Memory { get; set; } = 14;
    public int MessagesPerSession { get; set; } = 60;
    public int MessagesPerDay { get; set; } = 3000;
}

/// <summary>"Renders" section: AI renders from the studio (3D view or path-traced frame).</summary>
public class RenderOptions
{
    public int PerSession { get; set; } = 3;
    public int PerIp { get; set; } = 6;
    public int PerDay { get; set; } = 150;
    public string Model { get; set; } = "gpt-image-2";
    public string Size { get; set; } = "1536x1024";
    public int Compression { get; set; } = 88;
    /// <summary>largest image accepted, in base64 characters</summary>
    public int MaxImageChars { get; set; } = 4_000_000;
    public int MaxRefChars { get; set; } = 1_500_000;
}

/// <summary>"Redesign" section: room-photo redesigns and the render wallet.</summary>
public class RedesignOptions
{
    public int FreePerVisitor { get; set; } = 1;
    public int FreePerIp { get; set; } = 2;
    public int FreePerDay { get; set; } = 60;
    /// <summary>free renders: cheap model, low input fidelity</summary>
    public string FreeModel { get; set; } = "gpt-image-1-mini";
    public string FreeFidelity { get; set; } = "low";
    /// <summary>paid renders keep the room more faithfully</summary>
    public string PaidModel { get; set; } = "gpt-image-1";
    public string PaidFidelity { get; set; } = "high";
    public string Quality { get; set; } = "medium";
    public int Compression { get; set; } = 82;
    public int MaxImageChars { get; set; } = 3_000_000;
}

/// <summary>"PlanAi" section: Claude for the plan designer.</summary>
public class PlanAiOptions
{
    public string Model { get; set; } = "claude-sonnet-5";
    public int BriefsPerIp { get; set; } = 40;
    public int BriefsPerDay { get; set; } = 1500;
    public int ImagesPerIp { get; set; } = 8;
    public int ImagesPerDay { get; set; } = 300;
    public int MaxImageChars { get; set; } = 5_500_000;
}

/// <summary>"Forms" section: daily limits and models for the smaller forms.</summary>
public class FormLimitOptions
{
    public int ListingsPerIp { get; set; } = 5;
    public string ListingModel { get; set; } = "claude-haiku-5-5";
    public int FinishingPerIp { get; set; } = 3;
    public int BuildQuotesPerIp { get; set; } = 5;
    public int BuildQuotesPerDay { get; set; } = 300;
}

/// <summary>"Jobs" section: scheduled emails and the monthly cost research.</summary>
public class JobOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Riyadh hours</summary>
    public int DailyHour { get; set; } = 10;
    public int WeeklyHour { get; set; } = 9;
    public int MonthlyHour { get; set; } = 9;
    public string ResearchModel { get; set; } = "claude-sonnet-5";
    public string CaptionsModel { get; set; } = "claude-sonnet-5";
    /// <summary>signs opt-out links; falls back to Jwt:Key when empty</summary>
    public string OptOutSecret { get; set; } = "";
}
