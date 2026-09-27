using Investment.Domain.Research;
using Microsoft.EntityFrameworkCore;

namespace Investment.Persistence;

public static class StatusRollup
{
    /// <summary>Recomputes a strategy definition's status from its versions (call after saving version changes).</summary>
    public static async Task RollupStrategyStatusAsync(this InvestmentDbContext db, string strategyId, CancellationToken ct = default)
    {
        var statuses = await db.StrategyVersions.Where(v => v.StrategyId == strategyId).Select(v => v.Status).ToListAsync(ct);
        var def = await db.Strategies.SingleAsync(s => s.Id == strategyId, ct);
        var rolled = StrategyStatusRules.Rollup(statuses);
        if (def.Status == rolled) return;
        def.Status = rolled;
        def.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
