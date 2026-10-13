using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.IdentityModel.Tokens;
using Oxira.Design.Api.Infrastructure;
using Oxira.Design.Application;
using Oxira.Design.Application.Common;
using Oxira.Design.Infrastructure;
using Oxira.Design.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.AddApplication(cfg);
builder.Services.AddInfrastructure(cfg);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
// opt-out links are signed with Jobs:OptOutSecret, or the shared JWT key when none is set
builder.Services.PostConfigure<JobOptions>(o => { if (string.IsNullOrEmpty(o.OptOutSecret)) o.OptOutSecret = cfg["Jwt:Key"] ?? ""; });
builder.Services.AddSingleton<IRealtime, SignalRRealtime>();
builder.Services.AddHostedService<JobsHost>();
builder.Services.AddExceptionHandler<ErrorHandler>();
builder.Services.AddProblemDetails();

// orders and deliveries carry plan files (up to ~20 MB); IIS has the same limit in web.config
const long MaxBody = 26 * 1024 * 1024;
builder.Services.Configure<FormOptions>(o => { o.MultipartBodyLengthLimit = MaxBody; o.ValueLengthLimit = 8 * 1024 * 1024; });
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxBody);

void Json(JsonSerializerOptions o)
{
    o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.PropertyNameCaseInsensitive = true;
    o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.NumberHandling = JsonNumberHandling.AllowReadingFromString;
    o.Converters.Add(new LooseStringConverter());
}
builder.Services.AddControllers(o => o.Filters.Add<ReasonStatusFilter>()).AddJsonOptions(o => Json(o.JsonSerializerOptions));
builder.Services.AddOpenApi(o => o.AddDocumentTransformer<OpenApiInfoTransformer>());
builder.Services.AddSignalR(o => o.EnableDetailedErrors = builder.Environment.IsDevelopment()).AddJsonProtocol(o =>
{
    o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// ───── JWT: validates Oxira account tokens (issued by oxira-web-api) ─────
builder.Services.Configure<JwtOptions>(cfg.GetSection("Jwt"));
var jwt = cfg.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer, ValidAudience = jwt.Audience, IssuerSigningKey = jwt.SigningKey(),
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(30), RoleClaimType = OxiraClaims.Role, NameClaimType = OxiraClaims.Email,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256], RequireExpirationTime = true, RequireSignedTokens = true,
    };
    o.Events = new JwtBearerEvents
    {
        // SignalR sends the token as ?access_token= (browsers cannot set headers on WebSockets)
        OnMessageReceived = ctx =>
        {
            var t = ctx.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(t) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs")) ctx.Token = t;
            return Task.CompletedTask;
        },
        OnChallenge = async ctx =>
        {
            ctx.HandleResponse();
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { success = false, reason = "session", message = "سجّل الدخول بحساب أوكسيرا" });
        },
        OnForbidden = async ctx =>
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { success = false, reason = "denied", message = "غير مسموح" });
        },
    };
});
builder.Services.AddAuthorization();

// ───── CORS: the site, GitHub Pages and local development ─────
var origins = cfg.GetSection("Cors:Origins").Get<string[]>() ?? ["https://design.oxira.sa"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

// ───── rate limits per IP ─────
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.OnRejected = async (ctx, ct) => await ctx.HttpContext.Response.WriteAsJsonAsync(new { success = false, reason = "limit", message = "محاولات كثيرة، انتظر قليلًا ثم حاول" }, ct);
    string Ip(HttpContext h) => h.RequestServices.GetRequiredService<ICurrentUser>().Ip;
    RateLimitPartition<string> Window(HttpContext h, string name, int perMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(Ip(h), _ => new FixedWindowRateLimiterOptions { PermitLimit = cfg.GetValue($"RateLimit:{name}", perMinute), Window = TimeSpan.FromMinutes(1) });
    o.AddPolicy("forms", h => Window(h, "FormsPerMinute", 10));
    o.AddPolicy("chat", h => Window(h, "ChatPerMinute", 20));
    o.AddPolicy("render", h => Window(h, "RenderPerMinute", 6));
    o.AddPolicy("ai", h => Window(h, "AiPerMinute", 10));
    o.AddPolicy("reads", h => Window(h, "ReadsPerMinute", 120));
});

var app = builder.Build();

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseHttpsRedirection();
app.UseCors();
app.UseMiddleware<BodyMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapHub<DesignHub>("/hubs/design");
app.MapGet("/health", () => Results.Ok(new { ok = true, at = DateTimeOffset.UtcNow })).ExcludeFromDescription();

// Swagger: /openapi/v1.json and the UI at /swagger (Swagger__Enabled=false turns both off)
if (cfg.GetValue("Swagger:Enabled", true))
{
    app.MapOpenApi();
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/openapi/v1.json", "Oxira Design API v1");
        o.DocumentTitle = "Oxira Design API";
        o.EnablePersistAuthorization();
    });
}

if (cfg.GetValue("Database:MigrateOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<DbInitializer>().RunAsync();
}

app.Run();

public partial class Program;
