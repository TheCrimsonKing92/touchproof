using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
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

    internal PreTesterResolverReplayArm(string path, Stream verifiedApp)
    {
        _context = new ReplayLoadContext(Path.GetFullPath(path));
        try
        {
            var resolver = _context.LoadFromStream(verifiedApp)
                .GetType(typeof(PreTesterReverseDependencySelections).FullName!, throwOnError: true)!;
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

    private sealed class ReplayLoadContext(string appPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _dependencies = new(appPath);

        protected override Assembly? Load(AssemblyName name)
        {
            var path = _dependencies.ResolveAssemblyToPath(name);
            if (path is not null) return LoadFromAssemblyPath(path);
            if (name.Name?.StartsWith("Mcg.AgentOrchestrator.", StringComparison.Ordinal) != true) return null;
            // A test build can sit alongside its dependencies without an App deps file.
            return LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(appPath)!, name.Name + ".dll"));
        }
    }
}
