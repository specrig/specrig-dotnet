using Microsoft.Extensions.Time.Testing;

namespace Specrig;

public sealed class Clock : TimeProvider
{
    FakeTimeProvider? stopped;

    TimeProvider Current => stopped ?? System;

    public void Set(DateTimeOffset at)
    {
        if (stopped is null)
        {
            stopped = new FakeTimeProvider(at);
        }
        else
        {
            stopped.SetUtcNow(at);
        }
    }

    public void Advance(TimeSpan by) =>
        (stopped ?? throw new InvalidOperationException("the clock follows the wall clock until a step sets it")).Advance(by);

    public override DateTimeOffset GetUtcNow() => Current.GetUtcNow();

    public override TimeZoneInfo LocalTimeZone => Current.LocalTimeZone;

    public override long TimestampFrequency => Current.TimestampFrequency;

    public override long GetTimestamp() => Current.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        Current.CreateTimer(callback, state, dueTime, period);
}
