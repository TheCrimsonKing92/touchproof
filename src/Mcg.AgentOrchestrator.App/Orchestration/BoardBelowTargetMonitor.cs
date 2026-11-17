using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class BoardBelowTargetMonitor
{
    internal const string ModeNotFile = "mode-not-file";
    internal const string DailyCapReached = "daily-cap-reached";
    internal const string AuthorRefusalStreak = "author-refusal-streak";
    internal const string NoReadyItem = "no-ready-item";

    private int _consecutive;
    private bool _alerted;
    private string? _lastReason;

    internal string? Observe(int count, int target, string? reason, DateTimeOffset now)
    {
        if (count >= target)
        {
            var recovered = _alerted;
            _consecutive = 0;
            _alerted = false;
            _lastReason = null;
            return recovered
                ? string.Create(CultureInfo.InvariantCulture, $"BOARD_TARGET_RECOVERED count={count} target={target}")
                : null;
        }

        if (reason is null)
        {
            // Work resuming breaks the streak, but only reaching the target ends an alert.
            _consecutive = 0;
            return null;
        }

        _consecutive = Math.Min(_consecutive + 1, 3);
        if ((!_alerted && _consecutive < 3) || (_alerted && reason == _lastReason)) return null;

        _alerted = true;
        _lastReason = reason;
        return string.Create(CultureInfo.InvariantCulture,
            $"BOARD_BELOW_TARGET count={count} target={target} reason={reason}");
    }
}
