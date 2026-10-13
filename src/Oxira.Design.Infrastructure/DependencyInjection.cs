using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Oxira.Design.Application.Common;
using Oxira.Design.Infrastructure.Data;
using Oxira.Design.Infrastructure.Services;

namespace Oxira.Design.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection s, IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Design") ?? throw new InvalidOperationException("ConnectionStrings:Design is missing");
        s.AddDbContext<DesignDbContext>(o => o.UseSqlServer(cs, sql => sql.EnableRetryOnFailure(3)));
        s.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<DesignDbContext>());
        s.AddScoped<DbInitializer>();
        s.AddScoped<IQuota, SqlQuota>();
        s.AddSingleton(TimeProvider.System);

        s.Configure<SmtpOptions>(cfg.GetSection("Smtp"));
        s.AddSingleton<EmailQueue>();
        s.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<EmailQueue>());
        s.AddHostedService<EmailWorker>();

        s.Configure<StorageOptions>(cfg.GetSection("Storage"));
        s.AddSingleton<IFileStore, DiskFileStore>();

        s.Configure<AnthropicOptions>(cfg.GetSection("Anthropic"));
        s.Configure<OpenAiOptions>(cfg.GetSection("OpenAi"));
        // the monthly cost research uses web search and can take minutes
        s.AddHttpClient<IAnthropic, AnthropicClient>(h => h.Timeout = TimeSpan.FromMinutes(10));
        s.AddHttpClient<IImageEditor, OpenAiImageEditor>(h => h.Timeout = TimeSpan.FromSeconds(240));

        s.Configure<PaymentOptions>(cfg.GetSection("Payments"));
        if (cfg.GetValue("Payments:Simulate", false))
        {
            s.AddSingleton<SimulatedPayments>();
            s.AddSingleton<IPayments>(sp => sp.GetRequiredService<SimulatedPayments>());
            s.AddSingleton<ISimulatedCheckout>(sp => sp.GetRequiredService<SimulatedPayments>());
        }
        else s.AddHttpClient<IPayments, MoyasarPayments>(h => h.Timeout = TimeSpan.FromSeconds(20));
        return s;
    }
}
