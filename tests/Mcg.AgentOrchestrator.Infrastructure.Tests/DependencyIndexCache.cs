using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

internal sealed class DependencyIndexCache
{
    private sealed record Image(string Identity, Func<ImmutableArray<DependencyDeclaration>> Read);

    private readonly Func<ImmutableArray<DependencyDeclaration>, DependencyIndex> compose;
    private readonly ConcurrentDictionary<string, Lazy<ImmutableArray<DependencyDeclaration>>> parsed = new();
    private readonly ConcurrentDictionary<string, Lazy<DependencyIndex>> indexes = new();
    private readonly ConcurrentDictionary<string, StrongBox<int>> builds = new();

    internal DependencyIndexCache(Func<ImmutableArray<DependencyDeclaration>, DependencyIndex> compose)
    {
        this.compose = compose;
    }

    public static DependencyIndexCache Process { get; } = new(types => new DependencyIndex(types));

    public static DependencyIndex Get(params Assembly[] assemblies) => Process.GetOrBuild(assemblies);

    public DependencyIndex GetOrBuild(params Assembly[] assemblies) =>
        GetOrBuild(assemblies.Select(FromAssembly).ToArray());

    public DependencyIndex GetOrBuildImages(params ReadOnlyMemory<byte>[] images) =>
        GetOrBuild(images.Select(FromMemory).ToArray());

    public int BuildCount(params Assembly[] assemblies) =>
        builds.TryGetValue(Key(assemblies.Select(FromAssembly)), out var count)
            ? Volatile.Read(ref count.Value)
            : 0;

    private DependencyIndex GetOrBuild(Image[] images)
    {
        var distinct = images.DistinctBy(image => image.Identity).ToArray();
        var key = Key(distinct);
        return indexes.GetOrAdd(key, _ => new Lazy<DependencyIndex>(() =>
        {
            var types = distinct.SelectMany(image => (IEnumerable<DependencyDeclaration>)parsed.GetOrAdd(
                image.Identity, _ => new Lazy<ImmutableArray<DependencyDeclaration>>(
                    image.Read, LazyThreadSafetyMode.ExecutionAndPublication)).Value).ToImmutableArray();
            var index = compose(types);
            Interlocked.Increment(ref builds.GetOrAdd(key, _ => new StrongBox<int>()).Value);
            return index;
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static Image FromAssembly(Assembly assembly)
    {
        if (assembly?.FullName is not { Length: > 0 } fullName)
            throw new ArgumentException("Each assembly must have a full name.", nameof(assembly));
        if (assembly.Location.Length == 0)
            throw new ArgumentException("An assembly without a file location must be supplied as an image.", nameof(assembly));
        var path = assembly.Location;
        return new Image($"file:{fullName}:{assembly.ManifestModule.ModuleVersionId:D}",
            () => DependencyIndexBuilder.ReadFile(path));
    }

    private static Image FromMemory(ReadOnlyMemory<byte> image)
    {
        var snapshot = image.ToArray();
        return new Image("image:" + Convert.ToHexString(SHA256.HashData(snapshot)),
            () => DependencyIndexBuilder.ReadImage(snapshot));
    }

    private static string Key(IEnumerable<Image> images) =>
        string.Join("\n", images.Select(image => image.Identity).Distinct(StringComparer.Ordinal));
}
