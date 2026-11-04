// Parallel-safe: timestamps and UTC belong to this fixture, independently adjustable.
internal sealed class AcceptanceLaneWaitTestClock : TimeProvider
{
    private long _timestamp;
    internal DateTimeOffset Utc { get; set; } = DateTimeOffset.UnixEpoch;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
    public override DateTimeOffset GetUtcNow() => Utc;
    internal void Advance(double seconds) => Interlocked.Add(ref _timestamp, (long)(seconds * TimestampFrequency));
}
