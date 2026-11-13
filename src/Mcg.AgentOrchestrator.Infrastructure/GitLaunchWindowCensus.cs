using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Hooks are process-wide; windows belong only to the execution flow that opened them.
internal sealed class GitLaunchWindowCensus : IDisposable
{
    internal sealed record Measure(long Launches, long Timed, double WallMs);
    internal sealed record Totals(string Name, Measure Total,
        IReadOnlyDictionary<string, Measure> Subcommands, IReadOnlyDictionary<string, Measure> CallSites);
    internal sealed record Tick(int Number, double ElapsedMs, long Launches, double WallMs, long Timed,
        IReadOnlyDictionary<string, long> Subcommands, IReadOnlyDictionary<string, long> CallSites);

    private readonly AsyncLocal<Window?> current = new();
    private Action<string>? launchHook, priorLaunch;
    private Func<string, Action<GitCli.GitProcessTiming>?>? completionHook, priorCompletion;
    private int disposed;

    private Window? ActiveWindow
    {
        get
        {
            var window = current.Value;
            while (window is not null && window.IsClosed) window = window.Parent;
            return window;
        }
    }

    internal static GitLaunchWindowCensus Install()
    {
        var census = new GitLaunchWindowCensus();
        Action<string>? prior;
        do
        {
            prior = Volatile.Read(ref GitCli.LaunchObserver);
            var captured = prior;
            census.launchHook = command =>
            {
                try { census.RecordLaunch(command); }
                finally { captured?.Invoke(command); }
            };
        } while (Interlocked.CompareExchange(ref GitCli.LaunchObserver, census.launchHook, prior) != prior);
        census.priorLaunch = prior;
        Func<string, Action<GitCli.GitProcessTiming>?>? previous;
        do
        {
            previous = Volatile.Read(ref GitCli.CompletionObserver);
            var captured = previous;
            census.completionHook = command =>
            {
                Action<GitCli.GitProcessTiming>? own = null;
                Action<GitCli.GitProcessTiming>? other = null;
                try { own = census.BeginTiming(command); }
                finally { other = captured?.Invoke(command); }
                if (own is null) return other;
                return timing => { try { own(timing); } finally { other?.Invoke(timing); } };
            };
        } while (Interlocked.CompareExchange(ref GitCli.CompletionObserver, census.completionHook, previous) != previous);
        census.priorCompletion = previous;
        return census;
    }

    internal Window OpenWindow(string name)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var window = new Window(this, name, ActiveWindow);
        current.Value = window;
        return window;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void RecordLaunch(string subcommand, string? caller = null)
    {
        var active = ActiveWindow;
        if (Volatile.Read(ref disposed) != 0 || active is null) return;
        caller = MethodName(caller ?? ResolveCaller());
        for (var window = active; window is not null; window = window.Parent)
            window.Record(MethodName(subcommand), caller, null);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal Action<GitCli.GitProcessTiming>? BeginTiming(string subcommand, string? caller = null)
    {
        var active = ActiveWindow;
        if (Volatile.Read(ref disposed) != 0 || active is null) return null;
        var windows = new List<Window>();
        for (var window = active; window is not null; window = window.Parent) windows.Add(window);
        var site = MethodName(caller ?? ResolveCaller());
        var command = MethodName(subcommand);
        var completed = 0;
        return timing =>
        {
            if (Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref completed, 1) != 0 ||
                !double.IsFinite(timing.WallMs) || timing.WallMs < 0) return;
            foreach (var window in windows) window.Record(command, site, timing.WallMs);
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (launchHook is not null) Interlocked.CompareExchange(ref GitCli.LaunchObserver, priorLaunch, launchHook);
        if (completionHook is not null) Interlocked.CompareExchange(ref GitCli.CompletionObserver, priorCompletion, completionHook);
    }

    internal sealed class Window(GitLaunchWindowCensus owner, string name, Window? parent) : IDisposable
    {
        private readonly object sync = new();
        private readonly Dictionary<string, Measure> commands = new(StringComparer.Ordinal), sites = new(StringComparer.Ordinal);
        private Measure total = new(0, 0, 0);
        private Totals? closed;
        internal Window? Parent => parent;
        internal bool IsClosed { get { lock (sync) return closed is not null; } }

        internal void Record(string command, string site, double? wall)
        {
            lock (sync)
            {
                if (closed is not null) return;
                total = Add(total, wall);
                commands[command] = Add(commands.GetValueOrDefault(command) ?? new(0, 0, 0), wall);
                sites[site] = Add(sites.GetValueOrDefault(site) ?? new(0, 0, 0), wall);
            }
        }

        private static Measure Add(Measure value, double? wall) => wall is { } ms
            ? value with { Timed = value.Timed + 1, WallMs = value.WallMs + ms }
            : value with { Launches = value.Launches + 1 };

        internal Totals Close()
        {
            lock (sync)
            {
                closed ??= new(name, total, new ReadOnlyDictionary<string, Measure>(new Dictionary<string, Measure>(commands)),
                    new ReadOnlyDictionary<string, Measure>(new Dictionary<string, Measure>(sites)));
                if (ReferenceEquals(owner.current.Value, this)) owner.current.Value = parent;
                return closed;
            }
        }
        public void Dispose() => Close();
    }

    // Strip sidecar attribution metadata (parent and subcommand); publish method names only.
    internal static string MethodName(string name)
    {
        name = name.Split('|')[0].Split("<-", StringSplitOptions.None)[0];
        if (name.StartsWith("src:", StringComparison.Ordinal)) name = name[4..];
        else if (name.StartsWith("test:", StringComparison.Ordinal)) name = name[5..];
        return new string(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '+' or '<' or '>' ? c : '_').ToArray());
    }

    private static string ResolveCaller()
    {
        foreach (var frame in new StackTrace(false).GetFrames())
        {
            var method = frame.GetMethod();
            if (method is null) continue;
            var type = method.DeclaringType;
            if (type is null || type == typeof(GitCli) || type.DeclaringType == typeof(GitCli) ||
                type == typeof(GitLaunchWindowCensus) || type.DeclaringType == typeof(GitLaunchWindowCensus) ||
                type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true) continue;
            if (method!.Name == "MoveNext" && type.DeclaringType is { } enclosing)
            {
                var source = enclosing.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                    .FirstOrDefault(m => m.GetCustomAttribute<StateMachineAttribute>()?.StateMachineType == type);
                if (source is not null) { method = source; type = enclosing; }
            }
            var methodName = method.Name;
            if (methodName.StartsWith('<') && methodName.IndexOf('>') is var end && end > 1)
                methodName = methodName[1..end];
            while (type.Name.StartsWith('<') && type.DeclaringType is { } parentType) type = parentType;
            return $"{type.FullName}.{methodName}";
        }
        return "unknown";
    }

    internal static string FormatCounts(IReadOnlyDictionary<string, long> counts, int top = 5) => counts.Count == 0 ? "none" :
        string.Join(',', counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Take(top).Select(p => $"{MethodName(p.Key)}:{p.Value.ToString(CultureInfo.InvariantCulture)}"));

    internal static string FormatTick(int tick, double elapsedMs, Totals totals) => FormattableString.Invariant(
        $"TICK_GIT_CENSUS tick={tick} elapsed_ms={elapsedMs:0.###} git_launches={totals.Total.Launches} git_wall_ms={totals.Total.WallMs:0.###} git_timed={totals.Total.Timed} git_subcommands={FormatCounts(totals.Subcommands.ToDictionary(p => p.Key, p => p.Value.Launches))} git_call_sites={FormatCounts(totals.CallSites.ToDictionary(p => p.Key, p => p.Value.Launches))}");

    internal static bool TryParseTick(string detail, out Tick? tick)
    {
        tick = null;
        var tokens = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens[0] != "TICK_GIT_CENSUS") return false;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in tokens.Skip(1))
        {
            var pair = token.Split('=', 2);
            if (pair.Length != 2 || !fields.TryAdd(pair[0], pair[1])) return false;
        }
        bool Number(string key, out double value) => double.TryParse(fields.GetValueOrDefault(key),
            NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value >= 0;
        bool Count(string key, out long value) => long.TryParse(fields.GetValueOrDefault(key),
            NumberStyles.None, CultureInfo.InvariantCulture, out value);
        if (!Count("tick", out var n) || n <= 0 || n > int.MaxValue || !Number("elapsed_ms", out var elapsed) ||
            !Count("git_launches", out var launches) || !Number("git_wall_ms", out var wall) ||
            !Count("git_timed", out var timed) || timed > launches ||
            !Counts("git_subcommands", out var commands) || !Counts("git_call_sites", out var sites)) return false;
        tick = new((int)n, elapsed, launches, wall, timed, commands, sites);
        return true;

        bool Counts(string key, out IReadOnlyDictionary<string, long> result)
        {
            var values = new Dictionary<string, long>(StringComparer.Ordinal);
            result = values;
            if (!fields.TryGetValue(key, out var text)) return false;
            if (text == "none") return true;
            foreach (var item in text.Split(','))
            {
                var pair = item.Split(':');
                if (pair.Length != 2 || pair[0].Length == 0 || MethodName(pair[0]) != pair[0] ||
                    !long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                    !values.TryAdd(pair[0], count)) return false;
            }
            return true;
        }
    }
}
