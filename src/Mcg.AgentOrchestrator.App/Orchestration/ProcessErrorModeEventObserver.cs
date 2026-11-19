using System.Threading.Channels;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ProcessErrorModeEventObserver : IDisposable
{
    private static readonly TimeSpan NoticeCooldown = TimeSpan.FromMinutes(1);
    private readonly Channel<string> _notices = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false
    });
    private readonly Task _consumer;

    internal ProcessErrorModeEventObserver(
        ConductEventLogWriter writer,
        TimeProvider? clock = null,
        Action<string>? beforeAppend = null)
    {
        _consumer = ConsumeAsync(writer, clock ?? TimeProvider.System, beforeAppend);
    }

    internal void Observe(string notice) => _notices.Writer.TryWrite(notice);

    public void Dispose()
    {
        _notices.Writer.TryComplete();
        _consumer.GetAwaiter().GetResult();
    }

    private async Task ConsumeAsync(ConductEventLogWriter writer, TimeProvider clock, Action<string>? beforeAppend)
    {
        var coolingDown = new HashSet<string>(StringComparer.Ordinal);
        var recentEmissions = new Queue<(string Notice, long Timestamp)>();
        await foreach (var notice in _notices.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var now = clock.GetTimestamp();
            while (recentEmissions.TryPeek(out var oldest) &&
                clock.GetElapsedTime(oldest.Timestamp, now) >= NoticeCooldown)
            {
                recentEmissions.Dequeue();
                coolingDown.Remove(oldest.Notice);
            }

            if (coolingDown.Contains(notice))
                continue;

            try
            {
                beforeAppend?.Invoke(notice);
                writer.Append("process-error-mode", goalId: null, detail: notice);
            }
            catch (Exception)
            {
                // A failed append does not consume this notice's cooldown.
                continue;
            }

            coolingDown.Add(notice);
            recentEmissions.Enqueue((notice, clock.GetTimestamp()));
        }
    }
}
