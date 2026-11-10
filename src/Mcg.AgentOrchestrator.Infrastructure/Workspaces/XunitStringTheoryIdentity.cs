using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

// The xUnit v3 default per-class identity recipe, restricted to one string argument.
// See xunit/xunit v3-3.2.2: UniqueIDGenerator, CollectionAttribute, and ArraySerializer.
// https://github.com/xunit/xunit/blob/v3-3.2.2/src/xunit.v3.common/Utility/UniqueIDGenerator.cs
// https://github.com/xunit/xunit/blob/v3-3.2.2/src/xunit.v3.common/Serialization/SerializationHelper.cs
// Every reconstructed UID must match trusted discovery before it can authorize any credit;
// other framework versions, collection factories, config identities, or serializers fail closed.
internal static class XunitStringTheoryIdentity
{
    internal static string? MethodUid(IReadOnlyList<string>? arguments, string root,
        string assemblyFullName, string className, string methodName,
        IReadOnlyDictionary<string, string>? assemblyIdentities = null)
    {
        var assemblyName = assemblyFullName.Split(',')[0];
        // A recorded origin is authoritative for reconstruction; a missing entry must
        // fail closed instead of substituting a different build's path or sidecar.
        var assemblyUid = assemblyIdentities is null
            ? AssemblyUid(arguments, root, assemblyName)
            : assemblyIdentities.GetValueOrDefault(assemblyName);
        if (string.IsNullOrWhiteSpace(assemblyUid)) return null;
        var typeUid = Hash(assemblyName, className);
        var collectionUid = Hash(assemblyUid, $"Test collection for {className} (id: {typeUid})", "");
        return Hash(Hash(collectionUid, className), methodName);
    }

    internal static IReadOnlyDictionary<string, string> CaptureAssemblyIdentities(
        IReadOnlyList<string> arguments, string root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in arguments.Where(argument => argument.EndsWith(".dll", StringComparison.Ordinal))
                     .Select(argument => Path.GetFileNameWithoutExtension(argument)).Distinct(StringComparer.Ordinal))
            if (AssemblyUid(arguments, root, name) is { } identity) result[name] = identity;
        return result;
    }

    private static string? AssemblyUid(IReadOnlyList<string>? arguments, string root, string assemblyName)
    {
        var assemblies = (arguments ?? []).Where(argument =>
            Path.GetFileName(argument) == assemblyName + ".dll").ToArray();
        if (assemblies.Length != 1) return null;
        try
        {
            var assemblyPath = Path.GetFullPath(assemblies[0], root);
            var identityFile = Path.ChangeExtension(assemblyPath, ".uniqueid");
            var assemblyUid = File.Exists(identityFile)
                ? File.ReadLines(identityFile).Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0)
                : null;
            assemblyUid ??= Hash(assemblyPath, "");
            return assemblyUid;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static string RowUid(string methodUid, string argument)
    {
        var serializedArray = "t:-4:System.Object\nr:6:1\ntl:6:1\nl0:6:1\nlb0:6:0\ni0:0:" + Base64(argument);
        return Hash(methodUid, "[]:" + Base64(serializedArray));
    }

    private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Hash(params string[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', values) + '\0'))).ToLowerInvariant();
}
