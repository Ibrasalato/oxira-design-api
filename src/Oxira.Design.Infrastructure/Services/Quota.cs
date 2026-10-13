using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Oxira.Design.Application.Common;
using Oxira.Design.Domain;
using Oxira.Design.Infrastructure.Data;

namespace Oxira.Design.Infrastructure.Services;

/// <summary>Daily counters in SQL Server: one MERGE per use, safe across several API instances.</summary>
public class SqlQuota(DesignDbContext db, TimeProvider clock) : IQuota
{
    private const string Consume = """
        MERGE [UsageCounters] WITH (HOLDLOCK) AS t
        USING (SELECT @k AS [Key], @d AS [Day]) AS s ON t.[Key] = s.[Key] AND t.[Day] = s.[Day]
        WHEN MATCHED AND t.[Count] < @limit THEN UPDATE SET [Count] = t.[Count] + 1
        WHEN NOT MATCHED AND @limit > 0 THEN INSERT ([Key], [Day], [Count]) VALUES (@k, @d, 1)
        OUTPUT inserted.[Count] AS [Value];
        """;

    private DateOnly Day => Riyadh.Today(clock);

    public async Task<int?> TryConsumeAsync(string key, int limit, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<int>(Consume,
            new SqlParameter("@k", key[..Math.Min(key.Length, 120)]), new SqlParameter("@d", Day.ToDateTime(TimeOnly.MinValue)) { SqlDbType = System.Data.SqlDbType.Date },
            new SqlParameter("@limit", limit)).ToListAsync(ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public Task RefundAsync(string key, CancellationToken ct = default) =>
        db.UsageCounters.Where(c => c.Key == key && c.Day == Day && c.Count > 0).ExecuteUpdateAsync(s => s.SetProperty(c => c.Count, c => c.Count - 1), ct);

    public async Task<int> UsedAsync(string key, CancellationToken ct = default) =>
        await db.UsageCounters.Where(c => c.Key == key && c.Day == Day).Select(c => (int?)c.Count).FirstOrDefaultAsync(ct) ?? 0;
}
