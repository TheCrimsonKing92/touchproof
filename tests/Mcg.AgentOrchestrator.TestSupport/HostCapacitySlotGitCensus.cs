using System.Collections.Frozen;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Test-only cumulative counts with independent snapshots for each slot hold.</summary>
public sealed class HostCapacitySlotGitCensus(Func<string> currentTestName)
{
    private readonly object sync = new();
    private readonly Dictionary<string, Dictionary<string, int>> counts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> callers = new(StringComparer.Ordinal);
    [ThreadStatic] private static long stackWalks;
    private static readonly ConcurrentDictionary<MethodBase, CallerFrame> callerFrames = new();

    private readonly record struct CallerFrame(string? Caller, bool IsLaunchBoundary = false);

    public static long StackWalksOnCurrentThread => stackWalks;

    // Assembly identity matters: test projects and fixture probes share the product prefix.
    public static IReadOnlySet<string> ProductAssemblyNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Mcg.AgentOrchestrator.Core", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.Execution",
        "Mcg.AgentOrchestrator.Infrastructure", "Mcg.AgentOrchestrator.Infrastructure.OperatorComms",
        "Mcg.AgentOrchestrator.Infrastructure.Providers"
    }.ToFrozenSet(StringComparer.Ordinal);

    public static HostCapacitySlotGitCensus Shared { get; } = new(HostCapacitySlotTimingWriter.CurrentTestName);

    public sealed record GitLaunchDelta(int Launches, IReadOnlyDictionary<string, int> Subcommands,
        IReadOnlyDictionary<string, int>? Callers = null);

    public static string CallerOf(string key)
    {
        var separator = key.IndexOf('|');
        var member = separator < 0 ? key : key[..separator];
        var parent = member.IndexOf("<-", StringComparison.Ordinal);
        return parent < 0 ? member : member[..parent];
    }

    public static string? ParentOf(string key)
    {
        var separator = key.IndexOf('|');
        var member = separator < 0 ? key : key[..separator];
        var parent = member.IndexOf("<-", StringComparison.Ordinal);
        return parent < 0 ? null : member[(parent + 2)..];
    }

    public static IReadOnlyDictionary<string, int> GroupByCaller(
        IReadOnlyDictionary<string, int> sources, bool includeParent = false)
    {
        var grouped = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, count) in sources)
        {
            var separator = key.IndexOf('|');
            var group = includeParent || separator < 0 ? key : CallerOf(key) + key[separator..];
            grouped[group] = checked(grouped.GetValueOrDefault(group) + count);
        }
        return grouped;
    }

    public static bool EnsureInstalled(string? destination) =>
        EnsureInstalled(destination, ref GitCli.LaunchObserver);

    // A ref target permits lifecycle checks without changing process-global instrumentation.
    public static bool EnsureInstalled(string? destination, ref Action<string>? target)
    {
        if (string.IsNullOrEmpty(destination)) return false;
        try
        {
            return Interlocked.CompareExchange(ref target, Shared.Record, null) is null;
        }
        catch (Exception error) { Diagnose(error); return false; }
    }

    public static void RecordProbeLaunch(string subcommand)
    {
        try { Volatile.Read(ref GitCli.LaunchObserver)?.Invoke(subcommand); }
        catch (Exception error) { Diagnose(error); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Record(string subcommand)
    {
        var caller = "test:<unknown>";
        try
        {
            stackWalks++;
            caller = ResolveCaller(new StackTrace(fNeedFileInfo: false));
        }
        catch (Exception error) { Diagnose(error); }
        Record(subcommand, caller);
    }

    // Also permits measuring aggregation alone, holding all work but attribution constant.
    public void Record(string subcommand, string caller)
    {
        try
        {
            var name = currentTestName();
            lock (sync)
            {
                if (!counts.TryGetValue(name, out var commands))
                    counts[name] = commands = new(StringComparer.Ordinal);
                if (!callers.TryGetValue(name, out var sources))
                    callers[name] = sources = new(StringComparer.Ordinal);
                var key = caller + "|" + subcommand;
                var commandCount = checked(commands.GetValueOrDefault(subcommand) + 1);
                var callerCount = checked(sources.GetValueOrDefault(key) + 1);
                commands[subcommand] = commandCount;
                sources[key] = callerCount;
            }
        }
        catch (Exception error) { Diagnose(error); }
    }

    public Window? Open(string testName)
    {
        try
        {
            lock (sync)
                return new Window(this, testName, counts.TryGetValue(testName, out var commands)
                    ? new(commands, StringComparer.Ordinal) : new(StringComparer.Ordinal),
                    callers.TryGetValue(testName, out var sources)
                    ? new(sources, StringComparer.Ordinal) : new(StringComparer.Ordinal));
        }
        catch (Exception error) { Diagnose(error); return null; }
    }

    public sealed class Window
    {
        private readonly HostCapacitySlotGitCensus census;
        private readonly string testName;
        private readonly Dictionary<string, int> initial;
        private readonly Dictionary<string, int> initialCallers;

        internal Window(HostCapacitySlotGitCensus census, string testName, Dictionary<string, int> initial,
            Dictionary<string, int> initialCallers)
        {
            this.census = census;
            this.testName = testName;
            this.initial = initial;
            this.initialCallers = initialCallers;
        }

        public GitLaunchDelta? Close()
        {
            try
            {
                lock (census.sync)
                {
                    var delta = new Dictionary<string, int>(StringComparer.Ordinal);
                    var total = 0;
                    if (census.counts.TryGetValue(testName, out var commands))
                        foreach (var (command, count) in commands)
                        {
                            var difference = count - initial.GetValueOrDefault(command);
                            if (difference == 0) continue;
                            delta[command] = difference;
                            total = checked(total + difference);
                        }
                    var callerDelta = new Dictionary<string, int>(StringComparer.Ordinal);
                    if (census.callers.TryGetValue(testName, out var sources))
                        foreach (var (source, count) in sources)
                        {
                            var difference = count - initialCallers.GetValueOrDefault(source);
                            if (difference != 0) callerDelta[source] = difference;
                        }
                    return new(total, delta, callerDelta);
                }
            }
            catch (Exception error) { Diagnose(error); return null; }
        }
    }

    private static string ResolveCaller(StackTrace trace)
    {
        string? caller = null;
        string? parent = null;
        foreach (var frame in trace.GetFrames())
        {
            var method = frame.GetMethod();
            if (method is null) continue;
            var resolved = callerFrames.GetOrAdd(method, ResolveFrame);
            // Forwarding observers execute inside the launch boundary. Discard their
            // frames even when the wrapper belongs to another test or assembly.
            if (resolved.IsLaunchBoundary) { caller = null; parent = null; }
            else if (resolved.Caller is { } member)
            {
                if (caller is null) caller = member;
                // Generated frames and overloads can resolve to the same source member.
                else if (parent is null && member != caller) parent = member;
            }
        }
        return caller is null ? "test:<unknown>" : parent is null ? caller :
            caller + "<-" + parent[(parent.IndexOf(':') + 1)..];
    }

    private static CallerFrame ResolveFrame(MethodBase method)
    {
        var type = method.DeclaringType;
        if (type is null) return default;
        var assembly = type.Assembly.GetName().Name ?? "";
        if (!assembly.StartsWith("Mcg.AgentOrchestrator", StringComparison.Ordinal)) return default;

        // MoveNext belongs to a state machine; use its source method (which may itself
        // be a generated lambda/local function) before unwrapping the generated name.
        var methodName = method!.Name;
        if (methodName == "MoveNext" && type.IsDefined(typeof(CompilerGeneratedAttribute), false) &&
            type.DeclaringType is { } enclosing)
        {
            var source = enclosing.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static).FirstOrDefault(candidate =>
                candidate.GetCustomAttribute<StateMachineAttribute>()?.StateMachineType is { } state &&
                (state == type || (type.IsGenericType && state == type.GetGenericTypeDefinition())));
            methodName = source?.Name ?? type.Name;
            if (source is not null) type = source.DeclaringType!;
        }
        var initializerLambda = methodName.StartsWith("<.cctor>b__", StringComparison.Ordinal) ||
            methodName.StartsWith("<.ctor>b__", StringComparison.Ordinal);
        methodName = SourceMethodName(methodName);
        while (type.DeclaringType is { } parent && type.IsDefined(typeof(CompilerGeneratedAttribute), false))
            type = parent;
        var outermost = type;
        while (outermost.DeclaringType is { } outer) outermost = outer;
        if (outermost == typeof(HostCapacitySlotGitCensus)) return default;
        if (outermost == typeof(GitCli)) return new(null, IsLaunchBoundary: true);
        if (type.FullName == "InfrastructureTestSupport" &&
            methodName.StartsWith("RunGitProbe", StringComparison.Ordinal)) return new(null, IsLaunchBoundary: true);

        // Attribute constructor-initialized delegates to the method invoking them.
        if (initializerLambda) return default;

        var names = new Stack<string>();
        for (var current = type; current is not null; current = current.DeclaringType)
            names.Push(current.Name.Split('`')[0]);
        var sourceType = (type.Namespace is { Length: > 0 } ns ? ns + "." : "") + string.Join(".", names);
        return new((ProductAssemblyNames.Contains(assembly) ? "src:" : "test:") + sourceType + "." + methodName);
    }

    private static string SourceMethodName(string name)
    {
        while (name.StartsWith('<'))
        {
            var depth = 0;
            var end = -1;
            for (var index = 0; index < name.Length; index++)
            {
                if (name[index] == '<') depth++;
                else if (name[index] == '>' && --depth == 0) { end = index; break; }
            }
            if (end <= 1) break;
            name = name[1..end];
        }
        return name;
    }

    private static void Diagnose(Exception error) =>
        HostCapacitySlotTimingWriter.Diagnose("git census", error.GetType().Name + ": " + error.Message);
}
