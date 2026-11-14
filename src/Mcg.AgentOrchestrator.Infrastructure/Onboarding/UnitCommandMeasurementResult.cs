namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>A successful observed duration or an explicit execution failure.</summary>
public sealed record UnitCommandMeasurementResult
{
    public const int MaxOutputTailLength = 8192;

    public double? Seconds { get; }
    public string? Failure { get; }
    public UnitCommandMeasurementFailureCause? Cause { get; }
    public string? OutputTail { get; }

    private UnitCommandMeasurementResult(double? seconds, string? failure,
        UnitCommandMeasurementFailureCause? cause, string? outputTail)
    {
        Seconds = seconds;
        Failure = failure;
        Cause = cause;
        OutputTail = outputTail;
    }

    public static UnitCommandMeasurementResult Succeeded(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        return new(seconds, null, null, null);
    }

    public static UnitCommandMeasurementResult Failed(string reason,
        UnitCommandMeasurementFailureCause cause, string? outputTail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!Enum.IsDefined(cause))
            throw new ArgumentOutOfRangeException(nameof(cause));
        outputTail ??= string.Empty;
        var start = Math.Max(0, outputTail.Length - MaxOutputTailLength);
        if (start > 0 && char.IsLowSurrogate(outputTail[start])) start++;
        return new(null, reason, cause, outputTail[start..]);
    }
}
