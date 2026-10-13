namespace Ovrilo.Modules.Identity.Persistence;

internal static class DatabaseTime
{
    /// <summary>
    /// Current UTC time truncated to PostgreSQL <c>timestamptz</c> precision (1 µs), so values returned
    /// before and after a database round trip are identical.
    /// </summary>
    public static DateTimeOffset GetDatabaseUtcNow(this TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();
        return now.AddTicks(-(now.Ticks % (TimeSpan.TicksPerMillisecond / 1000)));
    }
}
