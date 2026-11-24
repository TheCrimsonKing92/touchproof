using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.App.Orchestration.PreTesterResolverCorpus;
using Provenance = Mcg.AgentOrchestrator.App.Orchestration.PreTesterChangedFileGuardClasses.Provenance;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// The pinned optimized App and the original-loop replay build have the same resolver operations,
// but load their own types and binaries. Only the sealed inputs and serialized outputs cross arms.
internal sealed class PreTesterResolverReplayArm : IDisposable
{
    private const BindingFlags ResolverMembers = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private readonly ReplayLoadContext _context;
    private readonly string _project;
    private readonly MethodInfo _resolve;
    private readonly MethodInfo _complete;
    private readonly Type _descriptor;
    private readonly Type _costType;
    private readonly Type _costReaderType;

    internal Guid ModuleVersionId { get; }
    internal string? InformationalVersion { get; }

    internal PreTesterResolverReplayArm(ReplayBuild build, Stream verifiedApp)
    {
        _context = new ReplayLoadContext(Path.GetFullPath(build.AppPath), build.DependencySha256);
        try
        {
            var app = _context.LoadFromStream(verifiedApp);
            ModuleVersionId = app.ManifestModule.ModuleVersionId;
            InformationalVersion = app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var resolver = app.GetType(typeof(PreTesterReverseDependencySelections).FullName!, throwOnError: true)!;
            _project = resolver.GetField("Project", ResolverMembers)?.GetRawConstantValue() as string
                ?? throw new InvalidDataException("Replay build has no resolver project.");
            _resolve = resolver.GetMethod("FirstUnresolvedClass", ResolverMembers)
                ?? throw new InvalidDataException("Replay build has no offline matching operation.");
            _complete = resolver.GetMethod("CompleteSelection", ResolverMembers)
                ?? throw new InvalidDataException("Replay build has no offline selection operation.");
            _descriptor = _resolve.GetParameters()[1].ParameterType.GenericTypeArguments[0];
            _costReaderType = _complete.GetParameters()[4].ParameterType;
            _costType = _costReaderType.GenericTypeArguments[1];
        }
        catch
        {
            _context.Unload();
            throw;
        }
    }

    internal bool HasEmbeddedResolverSource(Stream appBytes, string resolverPath, string sha256)
    {
        appBytes.Position = 0;
        using var pe = new PEReader(appBytes, PEStreamOptions.LeaveOpen);
        var entries = pe.ReadDebugDirectory().Where(entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb).ToArray();
        if (entries.Length != 1)
            return false;
        using var provider = pe.ReadEmbeddedPortablePdbDebugDirectoryData(entries[0]);
        var metadata = provider.GetMetadataReader();
        return MatchesSource(_resolve) && MatchesSource(_complete);

        bool MatchesSource(MethodInfo method)
        {
            var handle = MetadataTokens.MethodDefinitionHandle(method.MetadataToken & 0x00ffffff);
            var debug = metadata.GetMethodDebugInformation(handle.ToDebugInformationHandle());
            return debug.GetSequencePoints().Any(point => !point.IsHidden) &&
                debug.GetSequencePoints().Where(point => !point.IsHidden).All(point =>
                {
                    var documentHandle = point.Document.IsNil ? debug.Document : point.Document;
                    if (documentHandle.IsNil) return false;
                    var document = metadata.GetDocument(documentHandle);
                    var name = metadata.GetString(document.Name).Replace('\\', '/');
                    return (name == resolverPath || name.EndsWith("/" + resolverPath, StringComparison.Ordinal))
                        && Convert.ToHexString(metadata.GetBlobBytes(document.Hash)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
                });
        }
    }

    internal ReplayDecision Evaluate(ReplayCase row)
    {
        var invocation = row.Invocation;
        if (!invocation.Project.Equals(_project, StringComparison.Ordinal))
            throw new InvalidDataException($"Unexpected replay project {invocation.Project}.");
        var inventory = Array.CreateInstance(_descriptor, row.Inventory.Length);
        for (var index = 0; index < row.Inventory.Length; index++)
            inventory.SetValue(Activator.CreateInstance(_descriptor, row.Inventory[index].FullName, null, null), index);

        // The optimized build takes a third, optional argument for capture. Replay passes null.
        object?[] matchArgs = _resolve.GetParameters().Length == 3
            ? [invocation.Names, inventory, null] : [invocation.Names, inventory];
        var unresolved = (string?)_resolve.Invoke(null, matchArgs);
        var cost = invocation.Cost;
        var typedCost = Activator.CreateInstance(_costType, cost?.Run ?? false, cost?.Line ?? "recorded-cost-unavailable");
        var readCost = Expression.Lambda(_costReaderType, Expression.Constant(typedCost, _costType),
            Expression.Parameter(typeof(IReadOnlyList<string>))).Compile(preferInterpretation: true);
        var diagnostics = new List<string>();
        var selected = (ITuple)_complete.Invoke(null,
            [invocation.Names, unresolved, invocation.Paths, (Action<string>)diagnostics.Add, readCost, null])!;
        return new(unresolved,
            JsonSerializer.SerializeToElement(selected[0]).Deserialize<FindingEvidenceSelection[]>()!,
            JsonSerializer.SerializeToElement(selected[1]).Deserialize<Provenance[]>()!, diagnostics.ToArray());
    }

    public void Dispose() => _context.Unload();

    private sealed class ReplayLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _dependencies;
        private readonly string _appDirectory;
        private readonly Dictionary<string, string> _hashes;

        internal ReplayLoadContext(string appPath, Dictionary<string, string>? hashes) : base(isCollectible: true)
        {
            _appDirectory = Path.GetDirectoryName(appPath)!;
            _hashes = new(StringComparer.OrdinalIgnoreCase);
            if (hashes is null)
                throw new InvalidDataException("Replay build has no dependency hashes.");
            foreach (var (file, hash) in hashes)
            {
                if (file != Path.GetFileName(file) || !file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    file.Equals(Path.GetFileName(appPath), StringComparison.OrdinalIgnoreCase) ||
                    hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit) || !_hashes.TryAdd(file, hash))
                    throw new InvalidDataException($"Invalid replay dependency identity for {file}.");
                using var dependency = OpenVerifiedDependency(Path.Combine(_appDirectory, file), hash);
            }
            _dependencies = new(appPath);
        }

        protected override Assembly? Load(AssemblyName name)
        {
            var file = name.Name + ".dll";
            var path = _dependencies.ResolveAssemblyToPath(name);
            if (path is null && File.Exists(Path.Combine(_appDirectory, file)))
                path = Path.Combine(_appDirectory, file);
            if (path is null && File.Exists(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), file)))
                return null;
            if (path is null || !_hashes.TryGetValue(file, out var hash) ||
                !string.Equals(Path.GetFullPath(path), Path.Combine(_appDirectory, file), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Replay dependency is outside the staged identity: {name}.");

            // Verify and load from the same read handle so replacement cannot change the bytes used.
            using var dependency = OpenVerifiedDependency(path, hash);
            return LoadFromStream(dependency);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
            throw new InvalidDataException($"Unexpected native replay dependency: {unmanagedDllName}.");

        private static FileStream OpenVerifiedDependency(string path, string hash)
        {
            var dependency = File.OpenRead(path);
            try
            {
                if (!Convert.ToHexString(SHA256.HashData(dependency)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Replay dependency digest does not match: {Path.GetFileName(path)}.");
                dependency.Position = 0;
                return dependency;
            }
            catch
            {
                dependency.Dispose();
                throw;
            }
        }
    }
}
