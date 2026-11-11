namespace Daksh.Platform.Infrastructure;

/// <summary>Thin wrapper over TimeProvider so platform code depends on one small interface instead of the BCL type everywhere.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
