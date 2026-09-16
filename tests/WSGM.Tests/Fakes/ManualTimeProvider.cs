namespace WSGM.Tests.Fakes;

/// <summary>A clock a test sets or advances by hand.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow()
    {
        return Now;
    }

    internal void Advance(TimeSpan delta)
    {
        Now += delta;
    }
}
