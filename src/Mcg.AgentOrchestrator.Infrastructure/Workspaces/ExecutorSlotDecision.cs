namespace Mcg.AgentOrchestrator.Infrastructure;

// Pure evidence/state transition. No filesystem, clock, dispatch or setting writes belong here.
internal static class ExecutorSlotDecision
{
    internal static ExecutorSlotDecisionResult Decide(IReadOnlyList<ExecutorSlotSample> samples,
        ExecutorSlotControllerSettings settings, ExecutorSlotControllerState? state,
        int currentSlots, bool pinned, DateTimeOffset now)
    {
        settings.Validate();
        var reset = state is null || state.ObservedSlots != currentSlots || state.Settings != settings;
        var initial = reset ? new ExecutorSlotControllerState(currentSlots, settings) : state!;
        var recent = samples.Where(s => s.ObservedAt >= now.AddHours(-settings.LookbackHours) && s.ObservedAt <= now)
            .OrderBy(s => s.ObservedAt).ThenBy(s => s.AttemptId, StringComparer.Ordinal).ToArray();
        var estimates = Enumerable.Range(1, 4).Select(k => Estimate(recent, recent, k, settings)).ToArray();
        var watermark = recent.Length == 0 ? initial.CompletedThrough :
            new ExecutorSlotWatermark(recent[^1].ObservedAt, recent[^1].AttemptId);
        if (reset && state is not null)
            initial = initial with { CompletedThrough = watermark };
        var next = initial;
        ExecutorSlotWindow? window = null;
        ExecutorSlotEstimate? baseline = null, candidate = null;
        var progress = 0;
        ExecutorSlotDecisionResult Result(string action, int target, string reason) => new(
            pinned ? "report-only" : action, action, currentSlots, target, pinned, reason,
            recent.Length, recent.Count(s => s.Concurrency is null),
            recent.GroupBy(s => s.Concurrency is null ? "unavailable" : s.IntervalSource, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal), estimates, window, baseline, candidate,
            progress, pinned ? initial : next);

        if (currentSlots is < 1 or > 4) return Result("hold", currentSlots, "current-slots-outside-controller-bounds");
        if (reset && state is not null) return Result("hold", currentSlots, "observed-setting-or-policy-changed");
        if (initial.Trial is { } trial)
        {
            if (trial.PriorSlots is < 1 or > 4 || trial.TargetSlots != currentSlots || Math.Abs(trial.TargetSlots - trial.PriorSlots) != 1)
                throw new InvalidOperationException("invalid-executor-slot-trial-state");
            var jobs = recent.Where(s => s.ObservedAt > trial.StartedAt).ToArray();
            progress = jobs.Length;
            if (jobs.Length < settings.TrialJobs) return Result("hold", currentSlots, "trial-in-progress");
            var selected = jobs.TakeLast(settings.TrialJobs).ToArray();
            var measured = Estimate(selected, recent, currentSlots, settings, trial.Baseline.Estimate);
            (baseline, candidate) = Compare(trial.Baseline.Estimate, measured);
            window = MakeWindow(selected, measured);
            if (baseline.Throughput is null || candidate.Throughput is null)
                return Result("hold", currentSlots, "trial-insufficient-comparable-evidence");
            var keep = candidate.Throughput > baseline.Throughput && candidate.Failures.NoWorseThan(baseline.Failures);
            var target = keep ? currentSlots : trial.PriorSlots;
            next = initial with { ObservedSlots = target, Trial = null, StableWindows = 0,
                PreviousWindow = keep ? window : null,
                CompletedThrough = watermark };
            return Result(keep ? "keep" : "revert", target,
                keep ? "throughput-improved-without-failure-rate-rise" : "throughput-or-failure-rate-regressed");
        }

        var uncounted = recent.Where(s => initial.CompletedThrough?.Precedes(s) ?? true).ToArray();
        if (uncounted.Length < settings.JobsPerWindow) return Result("hold", currentSlots, "insufficient-new-jobs");
        var selectedWindow = uncounted.TakeLast(settings.JobsPerWindow).ToArray();
        var estimate = Estimate(selectedWindow, recent, currentSlots, settings);
        window = MakeWindow(selectedWindow, estimate);
        var stable = 0;
        if (estimate.Throughput is not null)
        {
            stable = 1;
            if (initial.PreviousWindow is { } previous)
            {
                (baseline, candidate) = Compare(previous.Estimate, estimate);
                stable = baseline.Throughput is > 0 && candidate.Throughput is { } throughput &&
                    Math.Abs(throughput / baseline.Throughput.Value - 1) <= settings.StabilityTolerance &&
                    candidate.Failures.NoWorseThan(baseline.Failures) ? initial.StableWindows + 1 : 1;
            }
        }
        next = initial with { StableWindows = stable, PreviousWindow = window,
            CompletedThrough = watermark };
        if (estimate.Throughput is null) return Result("hold", currentSlots, "insufficient-duration-evidence");
        if (stable < settings.StableWindows) return Result("hold", currentSlots, "accumulating-stable-windows");

        var neighbours = new[] { currentSlots - 1, currentSlots + 1 }.Where(k => k is >= 1 and <= 4).ToArray();
        var better = neighbours.Select(k => (K: k, Pair: Compare(estimate, estimates[k - 1])))
            .Where(item => item.Pair.Baseline.Throughput is { } b && item.Pair.Trial.Throughput > b &&
                item.Pair.Trial.Failures.NoWorseThan(item.Pair.Baseline.Failures))
            .OrderByDescending(item => item.Pair.Trial.Throughput).ThenBy(item => item.K).ToArray();
        var unmeasured = neighbours.Where(k => Compare(estimate, estimates[k - 1]).Trial.Throughput is null)
            .OrderBy(k => Math.Sign(k - currentSlots) == -initial.LastDirection ? 0 : 1).ToArray();
        if (better.Length == 0 && unmeasured.Length == 0)
            return Result("hold", currentSlots, "no-improving-or-unmeasured-neighbour");
        var targetSlots = better.Length > 0 ? better[0].K : unmeasured[0];
        var direction = Math.Sign(targetSlots - currentSlots);
        next = next with { ObservedSlots = targetSlots, StableWindows = 0, LastDirection = direction,
            Trial = new(window.Id, currentSlots, targetSlots, now, window) };
        return Result(direction > 0 ? "trial-up" : "trial-down", targetSlots, "stable-window-neighbour-trial");
    }

    private static ExecutorSlotWindow MakeWindow(ExecutorSlotSample[] samples, ExecutorSlotEstimate estimate) =>
        new(samples[^1].ObservedAt.ToString("O") + ":" + samples[^1].AttemptId,
            samples.Select(s => s.AttemptId).ToArray(), samples[^1].ObservedAt, estimate);

    internal static ExecutorSlotEstimate Estimate(IReadOnlyList<ExecutorSlotSample> window,
        IReadOnlyList<ExecutorSlotSample> soloPopulation, int k, ExecutorSlotControllerSettings settings,
        ExecutorSlotEstimate? frozen = null)
    {
        var jobs = window.Where(s => s.Concurrency == k).ToArray();
        var lanes = window.Select(s => s.Lane).Append("focused-evidence").Concat(frozen?.Lanes.Select(l => l.Lane) ?? [])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(lane =>
            {
                var old = frozen?.Lanes.SingleOrDefault(l => l.Lane == lane);
                var solo = Durations(soloPopulation.Where(s => s.Lane == lane && s.Concurrency == 1));
                var durations = Durations(jobs.Where(s => s.Lane == lane));
                var laneJobs = jobs.Count(s => s.Lane == lane);
                var soloMedian = old?.SoloMedianSeconds ?? Median(solo);
                var soloCount = old?.SoloSamples ?? solo.Length;
                var median = Median(durations);
                var exclusion = soloCount < settings.MinimumSoloSamples ? "insufficient-solo-samples" :
                    durations.Length < settings.MinimumSamplesPerK ? "insufficient-k-samples" :
                    soloMedian is not > 0 || median is not > 0 ? "invalid-duration" : null;
                var slowdown = exclusion is null ? median / soloMedian : null;
                if (slowdown is { } value && (!double.IsFinite(value) || value <= 0))
                { slowdown = null; exclusion = "invalid-duration"; }
                return new ExecutorSlotLaneEstimate(lane, soloCount, durations.Length, laneJobs, soloMedian, median,
                    slowdown, exclusion is null ? laneJobs * soloMedian!.Value : 0, exclusion);
            }).ToArray();
        var failures = new ExecutorSlotFailureRates(jobs.Length, jobs.Count(s => s.Outcome == "lane-timeout"),
            jobs.Count(s => s.Outcome == "lease-expired"), jobs.Count(s => s.Outcome == "trx-incomplete"));
        return Aggregate(k, jobs.Length, failures, lanes);
    }

    // Use identical lane membership AND work weights on both sides of every comparison.
    internal static (ExecutorSlotEstimate Baseline, ExecutorSlotEstimate Trial) Compare(
        ExecutorSlotEstimate baseline, ExecutorSlotEstimate trial)
    {
        var names = baseline.Lanes.Select(l => l.Lane).Union(trial.Lanes.Select(l => l.Lane), StringComparer.Ordinal).ToArray();
        var left = new List<ExecutorSlotLaneEstimate>();
        var right = new List<ExecutorSlotLaneEstimate>();
        foreach (var name in names)
        {
            var b = baseline.Lanes.SingleOrDefault(l => l.Lane == name);
            var t = trial.Lanes.SingleOrDefault(l => l.Lane == name);
            var reason = b is null || t is null || b.Jobs == 0 || t.Jobs == 0
                ? "lane-not-in-both-windows" : b.Exclusion ?? t.Exclusion;
            var weight = reason is null ? (b!.Jobs + t!.Jobs) * b.SoloMedianSeconds!.Value : 0;
            left.Add((b ?? new(name, 0, 0, 0, null, null, null, 0, reason)) with { Weight = weight, Exclusion = reason });
            right.Add((t ?? new(name, 0, 0, 0, null, null, null, 0, reason)) with { Weight = weight, Exclusion = reason });
        }
        return (Aggregate(baseline.K, baseline.Samples, baseline.Failures, left),
            Aggregate(trial.K, trial.Samples, trial.Failures, right));
    }

    private static ExecutorSlotEstimate Aggregate(int k, int count, ExecutorSlotFailureRates failures,
        IReadOnlyList<ExecutorSlotLaneEstimate> lanes)
    {
        var eligible = lanes.Where(l => l.Exclusion is null && l.Slowdown is > 0 && l.Weight > 0).ToArray();
        var weight = eligible.Sum(l => l.Weight);
        double? slowdown = weight > 0 ? eligible.Sum(l => l.Slowdown!.Value * (l.Weight / weight)) : null;
        double? throughput = slowdown is > 0 ? k / slowdown : null;
        if (throughput is { } value && !double.IsFinite(value)) { throughput = null; slowdown = null; }
        return new(k, count, slowdown, throughput, failures, lanes);
    }

    private static double[] Durations(IEnumerable<ExecutorSlotSample> samples) => samples
        .Where(s => s.Outcome == "accepted" && s.RemoteSeconds is > 0 && double.IsFinite(s.RemoteSeconds.Value))
        .Select(s => s.RemoteSeconds!.Value).Order().ToArray();

    private static double? Median(double[] values) => values.Length == 0 ? null : values.Length % 2 == 1
        ? values[values.Length / 2] : values[values.Length / 2 - 1] / 2 + values[values.Length / 2] / 2;
}
