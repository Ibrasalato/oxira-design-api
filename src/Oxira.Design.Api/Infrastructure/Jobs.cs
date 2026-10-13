using MediatR;
using Microsoft.Extensions.Options;
using Oxira.Design.Application.Common;
using Oxira.Design.Application.Features.Growth;

namespace Oxira.Design.Api.Infrastructure;

/// <summary>
/// Scheduled jobs (replace the n8n schedule triggers): every minute it asks which jobs are due (daily growth emails 10:00 Riyadh,
/// weekly report Sunday 09:00, monthly cost research on the 1st 09:00); each period runs once (JobRuns table). Jobs__Enabled=false turns it off.
/// </summary>
public class JobsHost(IServiceScopeFactory scopes, IOptions<JobOptions> opt, ILogger<JobsHost> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!opt.Value.Enabled) { log.LogInformation("Scheduled jobs are off (Jobs:Enabled=false)"); return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var ran = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new RunDueJobsCommand(), stop);
                if (ran.Count > 0) log.LogInformation("Jobs ran: {Jobs}", string.Join(", ", ran));
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Scheduled jobs failed"); }
        } while (await timer.WaitForNextTickAsync(stop));
    }
}
