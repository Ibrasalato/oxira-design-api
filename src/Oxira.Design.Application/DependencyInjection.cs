using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Oxira.Design.Application.Common;
using Oxira.Design.Application.Features.Orders;

namespace Oxira.Design.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection s, IConfiguration cfg)
    {
        s.Configure<SiteOptions>(cfg.GetSection("Site"));
        s.Configure<AiOptions>(cfg.GetSection("Ai"));
        s.Configure<OrderOptions>(cfg.GetSection("Orders"));
        s.Configure<RenderOptions>(cfg.GetSection("Renders"));
        s.Configure<RedesignOptions>(cfg.GetSection("Redesign"));
        s.Configure<PlanAiOptions>(cfg.GetSection("PlanAi"));
        s.Configure<FormLimitOptions>(cfg.GetSection("Forms"));
        s.Configure<JobOptions>(cfg.GetSection("Jobs"));
        s.AddMediatR(c =>
        {
            c.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly);
            c.AddOpenBehavior(typeof(LoggingBehavior<,>));
        });
        return s;
    }
}
