namespace Mcg.AgentOrchestrator.App.Orchestration;

// Tick-local admission state. Reconciliation stays open while new acceptance is held.
internal sealed class ConductorAcceptanceAdmissionHold
{
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string> _append;
    private readonly Func<int> _activeAttempts;
    private readonly int _tick;
    private readonly FleetRestartAdmissionHold? _initialFleetHold;
    private bool _fleetHeld;
    private bool _fleetLineEmitted;
    private bool _fleetReadFailureEmitted;
    private bool _selfRelaunchLineEmitted;

    internal ConductorAcceptanceAdmissionHold(string directory, Func<DateTimeOffset> now,
        Action<string> append, Func<int> activeAttempts, int tick)
    {
        _directory = directory;
        _now = now;
        _append = append;
        _activeAttempts = activeAttempts;
        _tick = tick;
        // Only a hold seen BEFORE admission may be acknowledged in this tick.
        // A later arrival waits for the next tick, covering check-to-launch races.
        _initialFleetHold = ReadFleetHold();
    }

    internal bool Suppress(bool deferringStop, bool pendingSelfRelaunch)
    {
        if (pendingSelfRelaunch && !_selfRelaunchLineEmitted)
        {
            _selfRelaunchLineEmitted = true;
            _append(ConductorSelfRelaunchAdmissionHold.Format(_tick));
        }
        return FleetHeld() || deferringStop || pendingSelfRelaunch;
    }

    internal bool FleetHeld()
    {
        ReadFleetHold();
        if (_fleetHeld && !_fleetLineEmitted)
        {
            _fleetLineEmitted = true;
            _append($"ADMISSION tick={_tick} result=held reason=fleet-restart-drain");
        }
        return _fleetHeld;
    }

    internal void Acknowledge()
    {
        if (_initialFleetHold is null) return;
        try { _initialFleetHold.Acknowledge(_directory, _activeAttempts()); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _append($"FLEET_RESTART_HOLD_ACK_FAILED reason={ConductorDiagnosticText.BoundSingleLine(ex.Message)}");
        }
    }

    private FleetRestartAdmissionHold? ReadFleetHold()
    {
        try
        {
            var hold = FleetRestartAdmissionHold.ReadForAdmission(_directory, _now(), _append);
            _fleetHeld |= hold is not null;
            return hold;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _fleetHeld = true;
            if (!_fleetReadFailureEmitted)
            {
                _fleetReadFailureEmitted = true;
                _append($"FLEET_RESTART_HOLD_READ_FAILED reason={ConductorDiagnosticText.BoundSingleLine(ex.Message)}");
            }
            return null;
        }
    }
}
