using Microsoft.EntityFrameworkCore;
using Spiza.Api.Data;

namespace Spiza.Api.Services;

public sealed record ReadinessState(bool DatabaseAvailable, bool HasPriceData, bool InitialRefreshCompleted)
{
    public bool IsReady => DatabaseAvailable && HasPriceData && InitialRefreshCompleted;
}

public sealed class ReadinessService(
    IDbContextFactory<SpizaDbContext> dbFactory,
    InitialRefreshState initialRefreshState)
{
    public async Task<ReadinessState> CheckAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var databaseAvailable = await db.Database.CanConnectAsync(cancellationToken);
        if (!databaseAvailable)
        {
            return new ReadinessState(false, false, initialRefreshState.IsCompleted);
        }

        var hasPriceData = await db.PriceObservations.AsNoTracking().AnyAsync(cancellationToken);
        return new ReadinessState(true, hasPriceData, initialRefreshState.IsCompleted);
    }
}
