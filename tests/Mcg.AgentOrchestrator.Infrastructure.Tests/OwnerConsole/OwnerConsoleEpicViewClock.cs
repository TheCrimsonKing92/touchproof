internal sealed class OwnerConsoleEpicViewClock : TimeProvider
{
    internal DateTimeOffset Current = OwnerConsoleEpicViewFixture.Now;
    public override DateTimeOffset GetUtcNow() => Current;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(3), "Test", "Test");
}
