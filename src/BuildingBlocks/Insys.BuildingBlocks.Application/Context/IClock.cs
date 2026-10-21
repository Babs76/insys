namespace Insys.BuildingBlocks.Application.Context;

/// <summary>The current time, from <see cref="TimeProvider"/> so tests can control it.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Today's date in UTC. TODO(spec): business dates may need the tenant's time zone; the SRS does not say.
    /// </summary>
    DateOnly UtcToday => DateOnly.FromDateTime(UtcNow.UtcDateTime);
}

public sealed class SystemClock(TimeProvider timeProvider) : IClock
{
    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
}
