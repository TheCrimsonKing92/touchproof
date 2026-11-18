using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

// One sweep's remaining goal-root bytes; policy categories are mutually exclusive.
internal sealed class TerminalBuildRootRetainedBytes
{
    internal enum Category { NotTerminal, RegisteredOwner, LiveOwner, Window }

    internal readonly record struct Measurement(long? Bytes, bool Complete)
    {
        public string Format() => Bytes is { } bytes
            ? (Complete ? "" : ">=") + bytes.ToString(CultureInfo.InvariantCulture)
            : "unknown";

        public Measurement Plus(Measurement other) => new(
            Bytes is null && other.Bytes is null ? null : checked((Bytes ?? 0) + (other.Bytes ?? 0)),
            Complete && other.Complete);
    }

    private Measurement _total = new(0, true);
    private readonly Dictionary<Category, Measurement> _categories = [];
    private int _rootCount;

    public long? GoalRootBytes => _total.Bytes;
    public long? NotTerminalBytes => Get(Category.NotTerminal).Bytes;
    public long? RegisteredOwnerBytes => Get(Category.RegisteredOwner).Bytes;
    public long? LiveOwnerBytes => Get(Category.LiveOwner).Bytes;
    public long? WindowBytes => Get(Category.Window).Bytes;
    public long? OwnerBytes => Owners().Bytes;
    public int IncompleteRoots { get; private set; }

    public void Add(Measurement size, Category? category)
    {
        _total = _rootCount++ == 0 ? size : _total.Plus(size);
        if (!size.Complete) IncompleteRoots++;
        if (category is { } value)
            _categories[value] = _categories.TryGetValue(value, out var previous) ? previous.Plus(size) : size;
    }

    private Measurement Get(Category category) => _categories.GetValueOrDefault(category, new(0, true));

    private Measurement Owners() => (_categories.ContainsKey(Category.RegisteredOwner),
        _categories.ContainsKey(Category.LiveOwner)) switch
    {
        (true, true) => Get(Category.RegisteredOwner).Plus(Get(Category.LiveOwner)),
        (true, false) => Get(Category.RegisteredOwner),
        (false, true) => Get(Category.LiveOwner),
        _ => new(0, true)
    };

    public string FormatReceiptFields(long? budget) => FormattableString.Invariant(
        $"size-budget-bytes={budget?.ToString(CultureInfo.InvariantCulture) ?? "none"} goal-root-bytes={_total.Format()} retained-not-terminal-bytes={Get(Category.NotTerminal).Format()} retained-window-bytes={Get(Category.Window).Format()} retained-owner-bytes={Owners().Format()} incomplete-roots={IncompleteRoots}");

    public static Measurement Measure(string root)
    {
        long bytes = 0;
        var complete = true;
        var observed = false;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            try
            {
                // Check every directory again on entry, including the discovered root.
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                        else
                        {
                            bytes = checked(bytes + new FileInfo(entry).Length);
                            observed = true;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        complete = false;
                    }
                }
                observed = true; // An empty, readable directory is a measured zero.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                complete = false;
            }
        }
        return new(observed || complete ? bytes : null, complete);
    }
}
