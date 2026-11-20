using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

internal sealed class AssemblyLookup(MetadataTypes primary, string? directory) : IDisposable
{
    private readonly Dictionary<string, MetadataTypes?> readers = new(StringComparer.Ordinal);
    private readonly List<PEReader> owned = [];
    private readonly Dictionary<(MetadataTypes, TypeDefinitionHandle), ImmutableArray<DependencyType>> interfaces = [];

    public ImmutableArray<DependencyType> InterfacesOf(MetadataTypes metadata, TypeDefinitionHandle handle,
        DependencyType? baseType)
    {
        var key = (metadata, handle);
        if (interfaces.TryGetValue(key, out var cached))
            return cached;

        var result = new HashSet<DependencyType>();
        var targets = metadata.Targets;
        metadata.Targets = result;
        try
        {
            foreach (var implementation in metadata.Reader.GetTypeDefinition(handle).GetInterfaceImplementations())
                metadata.GetType(metadata.Reader.GetInterfaceImplementation(implementation).Interface);
        }
        finally
        {
            metadata.Targets = targets;
        }

        if (baseType is not null && Find(baseType) is { } parent)
        {
            var baseDefinition = parent.Metadata.Reader.GetTypeDefinition(parent.Handle);
            var ancestor = parent.Metadata.Resolve(baseDefinition.BaseType);
            result.UnionWith(InterfacesOf(parent.Metadata, parent.Handle, ancestor));
        }

        var values = result.ToImmutableArray();
        interfaces.Add(key, values);
        return values;
    }

    public PrimitiveTypeCode EnumType(DependencyType type)
    {
        if (Find(type) is not { } definition)
            return PrimitiveTypeCode.Int32;

        var reader = definition.Metadata.Reader;
        foreach (var handle in reader.GetTypeDefinition(definition.Handle).GetFields())
        {
            var field = reader.GetFieldDefinition(handle);
            if (!reader.StringComparer.Equals(field.Name, "value__"))
                continue;
            var signature = reader.GetBlobReader(field.Signature);
            signature.ReadSignatureHeader();
            return (PrimitiveTypeCode)signature.ReadSignatureTypeCode();
        }
        throw new BadImageFormatException($"Enum {type.FullName} has no underlying type.");
    }

    private (MetadataTypes Metadata, TypeDefinitionHandle Handle)? Find(DependencyType type)
    {
        var assembly = type.AssemblyName;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(assembly) && Open(assembly) is { } metadata)
        {
            var handle = metadata.FindDefinition(type.FullName);
            if (!handle.IsNil)
                return (metadata, handle);

            var forwarded = FindForwarder(metadata, type);
            if (forwarded is null)
                break;
            assembly = forwarded;
        }
        return null;
    }

    private static string? FindForwarder(MetadataTypes metadata, DependencyType type)
    {
        var reader = metadata.Reader;
        var root = type.OutermostName;
        var rootName = root[(type.Namespace.Length == 0 ? 0 : type.Namespace.Length + 1)..];
        foreach (var handle in reader.ExportedTypes)
        {
            var exported = reader.GetExportedType(handle);
            if (exported.Implementation.Kind == HandleKind.AssemblyReference
                && reader.StringComparer.Equals(exported.Name, rootName)
                && reader.StringComparer.Equals(exported.Namespace, type.Namespace))
                return reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)exported.Implementation).Name);
        }
        return null;
    }

    private MetadataTypes? Open(string assembly)
    {
        if (assembly == primary.AssemblyName)
            return primary;
        if (readers.TryGetValue(assembly, out var metadata))
            return metadata;
        if (Path.GetFileName(assembly) != assembly)
            return null;
        var file = assembly + ".dll";
        var local = directory is null ? null : Path.Combine(directory, file);
        var path = File.Exists(local) ? local! : Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), file);
        if (File.Exists(path))
        {
            var pe = new PEReader(File.OpenRead(path), PEStreamOptions.PrefetchMetadata);
            owned.Add(pe);
            if (pe.HasMetadata)
                metadata = new MetadataTypes(pe.GetMetadataReader());
        }
        readers.Add(assembly, metadata);
        return metadata;
    }

    public void Dispose()
    {
        foreach (var reader in owned)
            reader.Dispose();
    }
}
