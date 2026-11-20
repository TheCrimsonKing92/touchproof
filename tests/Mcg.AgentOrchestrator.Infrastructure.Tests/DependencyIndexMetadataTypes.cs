using System.Collections.Immutable;
using System.Reflection.Metadata;

internal sealed class MetadataTypes : ISignatureTypeProvider<DependencyType?, object?>
{
    private static readonly DependencyType?[] Primitives = CreatePrimitives();

    private readonly Dictionary<EntityHandle, DependencyType> types = [];
    private readonly Dictionary<EntityHandle, bool> typeParameters = [];
    private Dictionary<string, TypeDefinitionHandle>? definitions;

    public MetadataTypes(MetadataReader reader)
    {
        if (!reader.IsAssembly)
            throw new BadImageFormatException("The dependency index requires assembly metadata.");
        Reader = reader;
        AssemblyName = reader.GetString(reader.GetAssemblyDefinition().Name);
    }

    public MetadataReader Reader { get; }
    public string AssemblyName { get; }
    public HashSet<DependencyType>? Targets { get; set; }

    public DependencyType? GetType(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition or HandleKind.TypeReference when !handle.IsNil => Add(GetNamedType(handle)),
        HandleKind.TypeSpecification => Reader.GetTypeSpecification((TypeSpecificationHandle)handle)
            .DecodeSignature(this, null),
        _ => null
    };

    public DependencyType? Resolve(EntityHandle handle)
    {
        var targets = Targets;
        Targets = null;
        try
        {
            return GetType(handle);
        }
        finally
        {
            Targets = targets;
        }
    }

    public DependencyType? Add(DependencyType? type)
    {
        if (type is not null && !type.Name.StartsWith('<') && !type.Name.StartsWith('!')
            && !type.FullName.Contains("+<", StringComparison.Ordinal)
            && !type.FullName.Contains("+!", StringComparison.Ordinal))
            Targets?.Add(type);
        return type;
    }

    public void AddSerializedType(TypeName name)
    {
        if (name.IsArray || name.IsPointer || name.IsByRef)
        {
            AddSerializedType(name.GetElementType());
            return;
        }
        if (name.IsConstructedGenericType)
        {
            foreach (var argument in name.GetGenericArguments())
                AddSerializedType(argument);
            name = name.GetGenericTypeDefinition();
        }

        var outermost = name;
        while (outermost.IsNested)
            outermost = outermost.DeclaringType;

        Add(new DependencyType(name.AssemblyName?.Name ?? AssemblyName, outermost.Namespace, name.Name, name.FullName));
    }

    public TypeDefinitionHandle FindDefinition(string fullName)
    {
        definitions ??= Reader.TypeDefinitions.ToDictionary(handle => GetNamedType(handle).FullName);
        return definitions.GetValueOrDefault(fullName);
    }

    public bool HasTypeParameter(EntityHandle constructor)
    {
        if (typeParameters.TryGetValue(constructor, out var cached))
            return cached;

        var targets = Targets;
        Targets = null;
        try
        {
            var signature = constructor.Kind == HandleKind.MethodDefinition
                ? Reader.GetMethodDefinition((MethodDefinitionHandle)constructor).DecodeSignature(this, null)
                : Reader.GetMemberReference((MemberReferenceHandle)constructor).DecodeMethodSignature(this, null);
            var hasType = signature.ParameterTypes.Any(type => type?.FullName == "System.Type");
            typeParameters.Add(constructor, hasType);
            return hasType;
        }
        finally
        {
            Targets = targets;
        }
    }

    private DependencyType GetNamedType(EntityHandle handle)
    {
        if (types.TryGetValue(handle, out var cached))
            return cached;

        string name;
        string @namespace;
        DependencyType? parent = null;
        var assembly = AssemblyName;
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            var definition = Reader.GetTypeDefinition((TypeDefinitionHandle)handle);
            name = Reader.GetString(definition.Name);
            @namespace = Reader.GetString(definition.Namespace);
            if (definition.IsNested)
                parent = GetNamedType(definition.GetDeclaringType());
        }
        else
        {
            var reference = Reader.GetTypeReference((TypeReferenceHandle)handle);
            name = Reader.GetString(reference.Name);
            @namespace = Reader.GetString(reference.Namespace);
            var scope = reference.ResolutionScope;
            if (scope.Kind == HandleKind.TypeReference)
                parent = GetNamedType(scope);
            else if (scope.Kind == HandleKind.AssemblyReference)
                assembly = Reader.GetString(Reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
        }

        var type = parent is null
            ? new DependencyType(assembly, @namespace, name, @namespace.Length == 0 ? name : @namespace + "." + name)
            : new DependencyType(parent.AssemblyName, parent.Namespace, name, parent.FullName + "+" + name);
        types.Add(handle, type);
        return type;
    }

    public DependencyType? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        GetType(handle);
    public DependencyType? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        GetType(handle);
    public DependencyType? GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle,
        byte rawTypeKind) => GetType(handle);
    public DependencyType? GetPrimitiveType(PrimitiveTypeCode typeCode) => Add(Primitives[(int)typeCode]);
    public DependencyType? GetArrayType(DependencyType? element, ArrayShape shape) => element;
    public DependencyType? GetSZArrayType(DependencyType? element) => element;
    public DependencyType? GetByReferenceType(DependencyType? element) => element;
    public DependencyType? GetPointerType(DependencyType? element) => element;
    public DependencyType? GetPinnedType(DependencyType? element) => element;
    public DependencyType? GetModifiedType(DependencyType? modifier, DependencyType? element, bool isRequired) => element;
    public DependencyType? GetGenericInstantiation(DependencyType? type, ImmutableArray<DependencyType?> arguments) => type;
    public DependencyType? GetGenericMethodParameter(object? context, int index) => null;
    public DependencyType? GetGenericTypeParameter(object? context, int index) => null;
    public DependencyType? GetFunctionPointerType(MethodSignature<DependencyType?> signature) => null;

    private static DependencyType?[] CreatePrimitives()
    {
        var result = new DependencyType?[256];
        foreach (var code in Enum.GetValues<PrimitiveTypeCode>())
        {
            if (code == PrimitiveTypeCode.Void)
                continue;
            var name = code == PrimitiveTypeCode.TypedReference ? "TypedReference" : code.ToString();
            result[(int)code] = new DependencyType("System.Private.CoreLib", "System", name, "System." + name);
        }
        return result;
    }

    internal readonly record struct AttributeType(DependencyType? Type, TypeName? Serialized = null);

    internal sealed class AttributeTypes(MetadataTypes metadata, AssemblyLookup assemblies) :
        ICustomAttributeTypeProvider<AttributeType>
    {
        public AttributeType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            new(metadata.Resolve(handle));
        public AttributeType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            new(metadata.Resolve(handle));
        public AttributeType GetPrimitiveType(PrimitiveTypeCode typeCode) => new(Primitives[(int)typeCode]);
        public AttributeType GetSZArrayType(AttributeType element) => default;
        public AttributeType GetSystemType() => new(new DependencyType("System.Runtime", "System", "Type", "System.Type"));
        public bool IsSystemType(AttributeType type) => type.Type?.FullName == "System.Type";
        public AttributeType GetTypeFromSerializedName(string name) => new(null, TypeName.Parse(name));

        public PrimitiveTypeCode GetUnderlyingEnumType(AttributeType type) =>
            type.Type is { } definition ? assemblies.EnumType(definition) : PrimitiveTypeCode.Int32;
    }
}
