using System;
using System.Threading;

namespace WSGM.Device.Sdk.Lifecycle;

/// <summary>
///     A point in time on <see cref="ActiveClock" />: the moment by which an operation must have
///     finished, counting only time the process could run.
/// </summary>
/// <remarks>
///     A wall-clock deadline keeps running while Modern Standby has the process frozen, so an
///     operation that was mid-flight when the machine slept came back with its budget already spent
///     and failed on a machine that had just woken (Claw 2026-09-22, Xbox Ally X 2026-09-28). This
///     one resumes with the budget it had when the freeze began.
/// </remarks>
public readonly struct Deadline : IEquatable<Deadline>, IComparable<Deadline>
{
    private readonly long _ticks;

    private Deadline(long ticks)
    {
        _ticks = ticks;
    }

    /// <summary>A deadline that has already passed.</summary>
    public static Deadline Expired => new(long.MinValue);

    /// <summary>A deadline that never passes.</summary>
    public static Deadline Never => new(long.MaxValue);

    /// <summary>The time left before the deadline, never negative.</summary>
    public TimeSpan Remaining
    {
        get
        {
            if (_ticks == long.MaxValue)
            {
                return TimeSpan.MaxValue;
            }

            // Compared before subtracting: Expired's long.MinValue minus the clock wraps to a huge
            // positive span, and the expired deadline never expired.
            var now = ActiveClock.Now.Ticks;
            return _ticks > now ? TimeSpan.FromTicks(_ticks - now) : TimeSpan.Zero;
        }
    }

    /// <summary>Whether the deadline has passed.</summary>
    public bool HasExpired => Remaining == TimeSpan.Zero;

    /// <summary>A deadline <paramref name="budget" /> of active time from now.</summary>
    /// <param name="budget">How long the operation may take while the process can run.</param>
    /// <returns>The deadline.</returns>
    public static Deadline After(TimeSpan budget)
    {
        var now = ActiveClock.Now.Ticks;
        var add = Math.Max(0, budget.Ticks);
        return new Deadline(add >= long.MaxValue - now ? long.MaxValue : now + add);
    }

    /// <summary>The active-time deadline equivalent to a wall-clock one, measured from now.</summary>
    /// <param name="utc">The wall-clock moment, for callers whose own budget is wall time.</param>
    /// <returns>The deadline, already expired when <paramref name="utc" /> has passed.</returns>
    public static Deadline At(DateTimeOffset utc)
    {
        return After(utc - DateTimeOffset.UtcNow);
    }

    /// <summary>The earlier of two deadlines.</summary>
    /// <param name="other">The other deadline.</param>
    /// <returns>Whichever comes first.</returns>
    public Deadline Earliest(Deadline other)
    {
        return _ticks <= other._ticks ? this : other;
    }

    /// <summary>A source that cancels when the deadline passes or any linked token cancels.</summary>
    /// <param name="linked">Further tokens that cancel it.</param>
    /// <returns>The source, which the caller disposes. It is already cancelled when the deadline has passed.</returns>
    /// <remarks>
    ///     A timer on the wall clock would fire the moment a frozen process thaws, so the source is
    ///     cancelled by <see cref="ActiveClock" /> instead, within its tick.
    /// </remarks>
    public CancellationTokenSource CreateCancellationSource(params CancellationToken[] linked)
    {
        // CreateLinkedTokenSource refuses an empty list, which faulted a plugin restart at a wake.
        var source = linked.Length == 0
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(linked);
        if (HasExpired)
        {
            source.Cancel();
        }
        else if (_ticks != long.MaxValue)
        {
            ActiveClock.CancelAt(_ticks, source);
        }

        return source;
    }

    /// <inheritdoc />
    public bool Equals(Deadline other)
    {
        return _ticks == other._ticks;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is Deadline other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return _ticks.GetHashCode();
    }

    /// <inheritdoc />
    public int CompareTo(Deadline other)
    {
        return _ticks.CompareTo(other._ticks);
    }

    /// <summary>Whether two deadlines are the same moment.</summary>
    public static bool operator ==(Deadline left, Deadline right)
    {
        return left.Equals(right);
    }

    /// <summary>Whether two deadlines differ.</summary>
    public static bool operator !=(Deadline left, Deadline right)
    {
        return !left.Equals(right);
    }

    /// <summary>Whether the left deadline comes first.</summary>
    public static bool operator <(Deadline left, Deadline right)
    {
        return left._ticks < right._ticks;
    }

    /// <summary>Whether the left deadline comes later.</summary>
    public static bool operator >(Deadline left, Deadline right)
    {
        return left._ticks > right._ticks;
    }

    /// <summary>Whether the left deadline comes first or at the same moment.</summary>
    public static bool operator <=(Deadline left, Deadline right)
    {
        return left._ticks <= right._ticks;
    }

    /// <summary>Whether the left deadline comes later or at the same moment.</summary>
    public static bool operator >=(Deadline left, Deadline right)
    {
        return left._ticks >= right._ticks;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return HasExpired ? "expired" : $"{Remaining.TotalMilliseconds:F0} ms left";
    }
}
