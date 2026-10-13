namespace Oxira.Design.Api.Infrastructure;

/// <summary>Scheduled jobs (added in a later step).</summary>
public class JobsHost : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stop) => Task.CompletedTask;
}
