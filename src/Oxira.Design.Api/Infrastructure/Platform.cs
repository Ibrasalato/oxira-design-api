using System.Buffers;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Oxira.Design.Application.Common;
using Oxira.Design.Domain;

namespace Oxira.Design.Api.Infrastructure;

/// <summary>
/// "Jwt" section. Oxira accounts are issued by oxira-web-api; every Oxira API validates the same issuer, audience and key,
/// so one sign-in works on oxira.sa, design and events. Jwt__Key is set as an environment variable on the server.
/// </summary>
public class JwtOptions
{
    public string Issuer { get; set; } = "https://api.oxira.sa";
    public string Audience { get; set; } = "oxira";
    public string Key { get; set; } = "";
    public SymmetricSecurityKey SigningKey() => new(Encoding.UTF8.GetBytes(Key.Length >= 32 ? Key : throw new InvalidOperationException("Jwt:Key must be at least 32 characters")));
}

public static class OxiraClaims
{
    public const string Role = "role";
    public const string Email = "email";
    public const string Name = "name";
    /// <summary>Oxira team members (staff, admin) are design admins here</summary>
    public static readonly string[] StaffRoles = ["staff", "admin"];
}

public class HttpCurrentUser(IHttpContextAccessor http, IConfiguration cfg) : ICurrentUser
{
    private ClaimsPrincipal? User => http.HttpContext?.User;
    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true && !string.IsNullOrEmpty(Email);
    public string? Id => User?.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User?.FindFirstValue(ClaimTypes.NameIdentifier);
    public string? Email => (User?.FindFirstValue(OxiraClaims.Email) ?? User?.FindFirstValue(ClaimTypes.Email))?.Trim().ToLowerInvariant();
    public string? Name => User?.FindFirstValue(OxiraClaims.Name);
    public bool IsStaff => User?.Claims.Any(c => c.Type is OxiraClaims.Role or ClaimTypes.Role && OxiraClaims.StaffRoles.Contains(c.Value)) == true;

    /// <summary>behind Cloudflare the visitor's address is in CF-Connecting-IP (only trusted when Proxy:TrustCloudflare is on)</summary>
    public string Ip
    {
        get
        {
            var ctx = http.HttpContext;
            if (ctx == null) return "unknown";
            if (cfg.GetValue("Proxy:TrustCloudflare", false) && ctx.Request.Headers.TryGetValue("CF-Connecting-IP", out var cf) && !string.IsNullOrWhiteSpace(cf))
                return cf.ToString()[..Math.Min(cf.ToString().Length, 64)];
            return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }
    }
}

/// <summary>Business errors become {"success":false,"reason":"…","message":"…"} with their status; anything else is logged and hidden.</summary>
public class ErrorHandler(ILogger<ErrorHandler> log, IHostEnvironment env) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception e, CancellationToken ct)
    {
        var (status, reason, message) = e switch
        {
            DomainException d => (d.Status, d.Reason, d.Message),
            DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } } => (409, "exists", "البيانات مسجلة مسبقًا"),
            BadHttpRequestException { StatusCode: 413 } => (413, "too_big", "الحجم كبير جدًا"),
            BadHttpRequestException b => (b.StatusCode, "invalid", "طلب غير صالح"),
            InvalidDataException => (400, "invalid", "طلب غير صالح"),
            OperationCanceledException => (499, "cancelled", "أُلغي الطلب"),
            _ => (500, "server", "حدث خطأ غير متوقع، حاول مرة أخرى"),
        };
        if (status >= 500) log.LogError(e, "Unhandled error on {Path}", ctx.Request.Path);
        ctx.Response.StatusCode = status;
        // development: the exception text helps while trying endpoints in Swagger (never in production)
        if (status >= 500 && env.IsDevelopment()) await ctx.Response.WriteAsJsonAsync(new { success = false, reason, message, error = e.ToString()[..Math.Min(e.ToString().Length, 3000)] }, ct);
        else await ctx.Response.WriteAsJsonAsync(new { success = false, reason, message }, ct);
        return true;
    }
}

/// <summary>A reply with success = false gets the HTTP status of its reason (limit → 429, not_found → 404 …); the body is kept.</summary>
public class ReasonStatusFilter : IAsyncResultFilter
{
    public Task OnResultExecutionAsync(ResultExecutingContext ctx, ResultExecutionDelegate next)
    {
        if (ctx.Result is ObjectResult { Value: IReply { Success: false } r } o && (o.StatusCode is null or 200))
            o.StatusCode = Reasons.Status(r.Reason);
        return next();
    }
}

/// <summary>
/// The site posts some JSON as text/plain on purpose (no CORS preflight) and the chat as a URL-encoded form.
/// Both are turned into JSON so the controllers read one format. Multipart uploads are left alone.
/// </summary>
public class BodyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var type = ctx.Request.ContentType;
        if (HttpMethods.IsPost(ctx.Request.Method) && type != null)
        {
            if (type.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
                ctx.Request.ContentType = "application/json; charset=utf-8";
            else if (type.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            {
                var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                var json = JsonSerializer.SerializeToUtf8Bytes(form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()));
                ctx.Request.Body = new MemoryStream(json);
                ctx.Request.ContentLength = json.Length;
                ctx.Request.ContentType = "application/json; charset=utf-8";
            }
        }
        await next(ctx);
    }
}

/// <summary>Reads numbers and booleans into string properties too (the site sends area: 120 or area: "120").</summary>
public class LooseStringConverter : JsonConverter<string>
{
    public override bool HandleNull => false;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number => Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray()),
        JsonTokenType.True => "true",
        JsonTokenType.False => "false",
        JsonTokenType.Null => null,
        _ => JsonDocument.ParseValue(ref reader).RootElement.GetRawText(),
    };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

/// <summary>OpenAPI document: title and the JWT bearer scheme (Swagger UI → Authorize).</summary>
internal sealed class OpenApiInfoTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Oxira Design API",
            Version = "v1",
            Description = "design.oxira.sa: المحادثة الذكية، طلبات التصميم والدفع عبر ميسر، رندرات الذكاء الاصطناعي ومحفظة الرندرات، مصمم المخططات، الحساب والمشاريع وبوابة المصممين، جولات الإعلانات، وحاسبة تكلفة البناء. الحساب يحتاج رمز حساب أوكسيرا (من oxira-web-api).",
        };
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", In = ParameterLocation.Header,
            Description = "Oxira account access token (from oxira-web-api /api/auth).",
        };
        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] });
        return Task.CompletedTask;
    }
}

/// <summary>Development and tests: an Oxira account token signed with the shared key (production tokens come from oxira-web-api).</summary>
public static class DevTokens
{
    public static string Issue(JwtOptions o, string email, string role, TimeSpan life)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()), new Claim(OxiraClaims.Email, email),
            new Claim(OxiraClaims.Name, email), new Claim(OxiraClaims.Role, role),
        };
        var token = new JwtSecurityToken(o.Issuer, o.Audience, claims, DateTime.UtcNow, DateTime.UtcNow.Add(life),
            new SigningCredentials(o.SigningKey(), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
