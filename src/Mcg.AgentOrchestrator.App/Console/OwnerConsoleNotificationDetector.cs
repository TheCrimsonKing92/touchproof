namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Session state records consumed crossings even while a tier is muted.
internal sealed class OwnerConsoleNotificationDetector(OwnerConsoleNotificationSettings initialSettings,
    Func<OwnerConsoleNotificationSettings>? currentSettings = null)
{
    private readonly Dictionary<string, (bool Eligible, bool Consumed)> _decisions = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastCalm;
    private DateTimeOffset? _restartAt;
    private DateTimeOffset? _tickReference;
    private bool _tickConsumed;
    private bool _initialized;
    private bool _stopSuppressed;
    private DateTimeOffset? _pendingStopAt;
    private bool? _lastKnownRunning;

    internal IReadOnlyList<OwnerConsoleNotificationTier> Detect(OwnerConsoleViewModel? previous,
        OwnerConsoleViewModel current, DateTimeOffset now, OwnerConsoleBellTiers enabled)
    {
        var settings = currentSettings?.Invoke() ?? initialSettings;
        if (previous is null || Unready(previous) || Unready(current))
        {
            Baseline(current, now, settings);
            return [];
        }
        if (!_initialized) Baseline(previous, now, settings);

        var before = KnownRunning(previous.Status.ConductorRunning) ?? _lastKnownRunning;
        var after = KnownRunning(current.Status.ConductorRunning) ?? before;
        _lastKnownRunning = after;
        var started = before == false && after == true;
        if (started) _restartAt = now;
        var calm = current.Status.LandedToday > previous.Status.LandedToday;
        var attention = ObserveDecisions(current, now, settings, baseline: false);
        var alarm = ObserveStop(before, after, current.Status, now, settings);
        alarm |= ObserveTick(current, after, now, settings, baseline: false);

        var result = new List<OwnerConsoleNotificationTier>(3);
        if (calm && enabled.Calm && (_lastCalm is null || now - _lastCalm >= settings.CalmCoalesce))
        {
            result.Add(OwnerConsoleNotificationTier.Calm);
            _lastCalm = now;
        }
        if (attention && enabled.Attention) result.Add(OwnerConsoleNotificationTier.Attention);
        if (alarm && enabled.Alarm) result.Add(OwnerConsoleNotificationTier.Alarm);
        return result;
    }

    private bool ObserveStop(bool? before, bool? after, OwnerConsoleStatus current, DateTimeOffset now,
        OwnerConsoleNotificationSettings settings)
    {
        if (after is null) return false;
        if (after == true)
        {
            _stopSuppressed = false;
            _pendingStopAt = null;
            return false;
        }
        if (before == true)
        {
            _stopSuppressed = current.PlannedStopReason is not null;
            _pendingStopAt = _stopSuppressed ? null : now;
            return false;
        }
        if (_pendingStopAt is { } pendingAt)
        {
            if (current.PlannedStopReason is not null)
            {
                _pendingStopAt = null;
                _stopSuppressed = true;
            }
            else
            {
                if (now - pendingAt < settings.UnplannedStopGrace) return false;
                // Consume the crossing even when the Alarm tier is muted.
                _pendingStopAt = null;
                return true;
            }
        }
        if (!_stopSuppressed) return false;
        if (current.PlannedStopReason is null ||
            current.PlannedStopReason == "self-relaunch-handoff" && current.PlannedStopAt is { } at &&
            now - at > settings.StaleTickThreshold)
        {
            // Consume the deferred stop even when the Alarm tier is muted.
            _stopSuppressed = false;
            return true;
        }
        return false;
    }

    private void Baseline(OwnerConsoleViewModel model, DateTimeOffset now, OwnerConsoleNotificationSettings settings)
    {
        _initialized = true;
        _lastKnownRunning = KnownRunning(model.Status.ConductorRunning) ?? _lastKnownRunning;
        _decisions.Clear();
        ObserveDecisions(model, now, settings, baseline: true);
        ObserveTick(model, _lastKnownRunning, now, settings, baseline: true);
    }

    private bool ObserveDecisions(OwnerConsoleViewModel model, DateTimeOffset now,
        OwnerConsoleNotificationSettings settings, bool baseline)
    {
        var live = model.Decisions.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _decisions.Keys.Where(id => !live.Contains(id)).ToArray()) _decisions.Remove(id);
        var ring = false;
        foreach (var decision in model.Decisions)
        {
            var fresh = decision.RaisedAt is { } raised && now - raised < settings.AttentionThreshold;
            if (!_decisions.TryGetValue(decision.Id, out var state))
            {
                _decisions[decision.Id] = (fresh, !fresh);
                continue;
            }
            if (state.Eligible && !state.Consumed && decision.RaisedAt is { } at &&
                now - at >= settings.AttentionThreshold)
            {
                _decisions[decision.Id] = (true, true);
                ring = !baseline;
            }
        }
        return ring;
    }

    private bool ObserveTick(OwnerConsoleViewModel model, bool? running, DateTimeOffset now,
        OwnerConsoleNotificationSettings settings, bool baseline)
    {
        if (running != true || model.Status.LatestTickAt is not { } tick) return false;
        var reference = _restartAt is { } restart && restart > tick ? restart : tick;
        var stale = now - reference > settings.StaleTickThreshold;
        // Older or temporarily unavailable ticks cannot re-arm a consumed reference.
        if (_tickReference is null || reference > _tickReference)
        {
            _tickReference = reference;
            _tickConsumed = stale;
            return false;
        }
        if (baseline)
        {
            _tickConsumed |= stale;
            return false;
        }
        if (reference != _tickReference || _tickConsumed || !stale) return false;
        _tickConsumed = true;
        return true;
    }

    private static bool? KnownRunning(ConductorLiveness reading) => reading switch
    {
        ConductorLiveness.Running => true,
        ConductorLiveness.NotRunning => false,
        ConductorLiveness.Unknown => null,
        _ => throw new ArgumentOutOfRangeException(nameof(reading), reading, null)
    };

    private static bool Unready(OwnerConsoleViewModel model) =>
        model.DecisionsState is { Loading: true } or { Error: not null } ||
        model.ActivityState is { Loading: true } or { Error: not null };
}
