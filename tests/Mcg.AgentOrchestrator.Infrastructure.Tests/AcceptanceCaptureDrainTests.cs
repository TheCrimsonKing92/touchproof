using Mcg.AgentOrchestrator.Infrastructure;
using System.Text;

// Parallel-safe: each drain owns its source and a unique capture file; no global state changes.
public sealed class AcceptanceCaptureDrainTests
{
    // Expectations come from ffbe89e0f's GoalAcceptanceVerifier.OutputCapture.cs and
    // GoalAcceptanceVerifier.CaptureFiles.cs, before the helpers moved.
    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(1)]
    [Xunit.InlineData(96 * 1024)]
    public async Task DrainBelowLimit_PreservesEveryByteWithoutLimitNotification(int length)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-drain-{Guid.NewGuid():N}.out");
        var payload = Enumerable.Range(0, length).Select(index => (byte)(index % 256)).ToArray();
        try
        {
            await using var source = new MemoryStream(payload);
            var notifications = 0;
            var result = await AcceptanceCaptureDrain.DrainCappedCaptureAsync(
                source,
                path,
                limitBytes: length + 1,
                utcNow: () => DateTimeOffset.UnixEpoch,
                onLimitReached: () => notifications++,
                CancellationToken.None);

            Xunit.Assert.Equal(path, result.Path);
            Xunit.Assert.False(result.LimitReached);
            Xunit.Assert.Equal((long)length, result.WrittenBytes);
            Xunit.Assert.Equal(0, notifications);
            Xunit.Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(4096)]
    [Xunit.InlineData(128 * 1024)]
    public async Task DrainPastLimit_CapsFileReportsAllBytesAndNotifiesOnce(int length)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-drain-{Guid.NewGuid():N}.out");
        var payload = Encoding.UTF8.GetBytes(new string('x', length));
        try
        {
            await using var source = new MemoryStream(payload);
            var notifications = 0;
            var result = await AcceptanceCaptureDrain.DrainCappedCaptureAsync(
                source,
                path,
                limitBytes: 512,
                utcNow: () => DateTimeOffset.UnixEpoch,
                onLimitReached: () => notifications++,
                CancellationToken.None);

            var bytes = await File.ReadAllBytesAsync(path);
            // The pre-move finalizer reserves space for this tail inside the 512-byte cap.
            var terminator = Encoding.UTF8.GetBytes(
                $"\n[ACCEPTANCE_CAPTURE_LIMIT_REACHED cap_bytes=512 written_bytes={length} timestamp=1970-01-01T00:00:00.0000000+00:00]\n");
            var expected = payload.Take(512 - terminator.Length).Concat(terminator).ToArray();

            Xunit.Assert.Equal(path, result.Path);
            Xunit.Assert.True(result.LimitReached);
            Xunit.Assert.Equal((long)length, result.WrittenBytes);
            Xunit.Assert.Equal((long)length, source.Position);
            Xunit.Assert.Equal(1, notifications);
            Xunit.Assert.Equal(512, bytes.Length);
            Xunit.Assert.Equal(expected, bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(null, false, 0, false)]
    [Xunit.InlineData(null, true, 0, true)]
    [Xunit.InlineData(null, false, 7, true)]
    [Xunit.InlineData(null, true, 7, true)]
    [Xunit.InlineData(true, false, 0, true)]
    [Xunit.InlineData(true, true, 0, true)]
    [Xunit.InlineData(true, false, 7, true)]
    [Xunit.InlineData(true, true, 7, true)]
    [Xunit.InlineData(false, false, 0, false)]
    [Xunit.InlineData(false, true, 0, false)]
    [Xunit.InlineData(false, false, 7, false)]
    [Xunit.InlineData(false, true, 7, false)]
    public void Retention_UsesExitAndTimeoutUnlessExplicitlyOverridden(
        bool? keepCaptureFiles, bool timedOut, int exitCode, bool expected)
    {
        Xunit.Assert.Equal(expected,
            AcceptanceCaptureDrain.ShouldKeepCaptureFiles(keepCaptureFiles, timedOut, exitCode));
    }
}
