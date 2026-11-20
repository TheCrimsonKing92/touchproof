using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

internal sealed class DependencyIndexBuilder
{
    private static readonly OperandType?[] SingleByteOperands = new OperandType?[256];
    private static readonly OperandType?[] DoubleByteOperands = new OperandType?[256];
    private static readonly FrozenSet<int> TypeReferenceOpcodes = new[]
    {
        OpCodes.Castclass, OpCodes.Isinst, OpCodes.Box, OpCodes.Newarr, OpCodes.Initobj, OpCodes.Unbox,
        OpCodes.Unbox_Any, OpCodes.Ldelem, OpCodes.Ldobj, OpCodes.Stelem, OpCodes.Ldelema, OpCodes.Stobj
    }.Select(opcode => (int)(ushort)opcode.Value).ToFrozenSet();

    static DependencyIndexBuilder()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode opcode)
                (opcode.Size == 1 ? SingleByteOperands : DoubleByteOperands)[opcode.Value & 0xff] = opcode.OperandType;
        }
    }

    private readonly PEReader pe;
    private readonly MetadataTypes metadata;
    private readonly AssemblyLookup assemblies;
    private readonly MetadataTypes.AttributeTypes attributeTypes;
    private readonly HashSet<DependencyType> references = [];
    private readonly HashSet<DependencyMember> calls = [];
    private readonly HashSet<MethodDefinitionHandle> visitedBodies = [];
    private readonly Dictionary<EntityHandle, DependencyMember> members = [];

    private DependencyIndexBuilder(PEReader pe, string? directory)
    {
        if (!pe.HasMetadata)
            throw new BadImageFormatException("The dependency index requires a managed assembly image.");
        this.pe = pe;
        metadata = new MetadataTypes(pe.GetMetadataReader());
        assemblies = new AssemblyLookup(metadata, directory);
        attributeTypes = new MetadataTypes.AttributeTypes(metadata, assemblies);
    }

    public static ImmutableArray<DependencyDeclaration> ReadFile(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        return new DependencyIndexBuilder(pe, Path.GetDirectoryName(path)).Read();
    }

    public static ImmutableArray<DependencyDeclaration> ReadImage(byte[] image)
    {
        using var pe = new PEReader(new MemoryStream(image, writable: false));
        return new DependencyIndexBuilder(pe, null).Read();
    }

    private ImmutableArray<DependencyDeclaration> Read()
    {
        using var lookup = assemblies;
        var reader = metadata.Reader;
        var types = ImmutableArray.CreateBuilder<DependencyDeclaration>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = metadata.Resolve(handle)!;
            if (IsGenerated(type) || type.FullName.StartsWith("Coverlet", StringComparison.Ordinal) ||
                type.FullName is "Microsoft.CodeAnalysis.EmbeddedAttribute"
                    or "System.Runtime.CompilerServices.NullableAttribute"
                    or "System.Runtime.CompilerServices.NullableContextAttribute")
                continue;

            references.Clear();
            metadata.Targets = references;
            var definition = reader.GetTypeDefinition(handle);
            var baseType = metadata.GetType(definition.BaseType);
            foreach (var interfaceType in assemblies.InterfacesOf(metadata, handle, baseType))
                references.Add(interfaceType);
            ReadAttributes(definition.GetCustomAttributes());
            ReadConstraints(definition.GetGenericParameters());

            foreach (var fieldHandle in definition.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                if (IsGeneratedName(reader.GetString(field.Name)))
                    continue;
                field.DecodeSignature(metadata, null);
                ReadAttributes(field.GetCustomAttributes());
            }

            foreach (var propertyHandle in definition.GetProperties())
            {
                var property = reader.GetPropertyDefinition(propertyHandle);
                if (IsGeneratedName(reader.GetString(property.Name)))
                    continue;
                property.DecodeSignature(metadata, null);
                ReadAttributes(property.GetCustomAttributes());
            }

            var methods = ImmutableArray.CreateBuilder<DependencyMethod>();
            foreach (var methodHandle in definition.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (IsGeneratedName(reader.GetString(method.Name)))
                    continue;

                method.DecodeSignature(metadata, null);
                ReadAttributes(method.GetCustomAttributes());
                foreach (var parameterHandle in method.GetParameters())
                    ReadAttributes(reader.GetParameter(parameterHandle).GetCustomAttributes());
                ReadConstraints(method.GetGenericParameters());

                visitedBodies.Clear();
                calls.Clear();
                ReadBody(methodHandle);
                if (calls.Count != 0)
                    methods.Add(new DependencyMethod(GetMember(methodHandle)!, [.. calls]));
            }

            types.Add(new DependencyDeclaration(type, [.. references], methods.ToImmutable()));
        }
        return types.ToImmutable();
    }

    private void ReadConstraints(GenericParameterHandleCollection parameters)
    {
        foreach (var handle in parameters)
        {
            var parameter = metadata.Reader.GetGenericParameter(handle);
            foreach (var constraint in parameter.GetConstraints())
                metadata.GetType(metadata.Reader.GetGenericParameterConstraint(constraint).Type);
            ReadAttributes(parameter.GetCustomAttributes());
        }
    }

    private void ReadAttributes(CustomAttributeHandleCollection attributes)
    {
        var reader = metadata.Reader;
        foreach (var handle in attributes)
        {
            var attribute = reader.GetCustomAttribute(handle);
            var member = GetMember(attribute.Constructor);
            metadata.Add(member?.DeclaringType);
            if (member is null || member.DeclaringType.FullName is
                "System.Runtime.CompilerServices.AsyncStateMachineAttribute" or
                "System.Runtime.CompilerServices.IteratorStateMachineAttribute" or
                "System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute")
                continue;

            // A fixed System.Type argument is the only attribute value used by the dependency rules.
            if (!metadata.HasTypeParameter(attribute.Constructor))
                continue;
            foreach (var argument in attribute.DecodeValue(attributeTypes).FixedArguments)
            {
                if (argument.Value is MetadataTypes.AttributeType { Serialized: { } name })
                    metadata.AddSerializedType(name);
            }
        }
    }

    private void ReadBody(MethodDefinitionHandle handle)
    {
        if (!visitedBodies.Add(handle))
            return;
        var reader = metadata.Reader;
        var method = reader.GetMethodDefinition(handle);
        if (method.RelativeVirtualAddress == 0)
            return;

        if (FindStateMachine(method) is { } machine)
        {
            foreach (var fieldHandle in reader.GetTypeDefinition(machine.Type).GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                var name = reader.GetString(field.Name);
                if (name is not ("<>1__state" or "<>2__current" or "<>l__initialThreadId" or "<>4__this" or "<>t__builder"))
                    field.DecodeSignature(metadata, null);
            }
            ReadBody(machine.MoveNext);
            return;
        }

        var body = pe.GetMethodBody(method.RelativeVirtualAddress);
        if (!body.LocalSignature.IsNil)
            reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(metadata, null);

        var il = body.GetILReader();
        while (il.RemainingBytes != 0)
        {
            var first = il.ReadByte();
            var opcode = first == 0xfe ? 0xfe00 | il.ReadByte() : first;
            var operand = first == 0xfe ? DoubleByteOperands[opcode & 0xff] : SingleByteOperands[opcode];
            switch (operand)
            {
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                case OperandType.InlineTok:
                    ReadToken(MetadataTokens.EntityHandle(il.ReadInt32()));
                    break;
                case OperandType.InlineType:
                    var token = il.ReadInt32();
                    // Cast, type check, and body-type operands used by the existing rules.
                    if (TypeReferenceOpcodes.Contains(opcode))
                        ReadToken(MetadataTokens.EntityHandle(token));
                    break;
                case OperandType.InlineSwitch:
                    var cases = il.ReadInt32();
                    il.Offset += checked(cases * 4);
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    il.Offset += 8;
                    break;
                case OperandType.InlineBrTarget:
                case OperandType.InlineI:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.ShortInlineR:
                    il.Offset += 4;
                    break;
                case OperandType.InlineVar:
                    il.Offset += 2;
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    il.Offset++;
                    break;
                case OperandType.InlineNone:
                    break;
                default:
                    throw new BadImageFormatException($"Unexpected operand type {operand} in IL.");
            }
        }
    }

    private (TypeDefinitionHandle Type, MethodDefinitionHandle MoveNext)? FindStateMachine(MethodDefinition method)
    {
        var reader = metadata.Reader;
        foreach (var attributeHandle in method.GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(attributeHandle);
            var name = GetMember(attribute.Constructor)?.DeclaringType.FullName;
            if (name is not ("System.Runtime.CompilerServices.AsyncStateMachineAttribute" or
                "System.Runtime.CompilerServices.IteratorStateMachineAttribute" or
                "System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute"))
                continue;

            var arguments = attribute.DecodeValue(attributeTypes).FixedArguments;
            if (arguments is [{ Value: MetadataTypes.AttributeType { Serialized: { } stateMachine } }]
                && metadata.FindDefinition(stateMachine.FullName) is { IsNil: false } handle)
            {
                foreach (var candidate in reader.GetTypeDefinition(handle).GetMethods())
                {
                    if (reader.StringComparer.Equals(reader.GetMethodDefinition(candidate).Name, "MoveNext"))
                        return (handle, candidate);
                }
            }
        }
        return null;
    }

    private void ReadToken(EntityHandle handle)
    {
        var reader = metadata.Reader;
        if (handle.Kind is HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification)
        {
            metadata.GetType(handle);
            return;
        }

        if (handle.Kind == HandleKind.MethodSpecification)
        {
            var method = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
            method.DecodeSignature(metadata, null);
            handle = method.Method;
        }

        if (handle.Kind == HandleKind.MemberReference)
        {
            var reference = reader.GetMemberReference((MemberReferenceHandle)handle);
            // A constructed declaring type contributes its generic arguments even when the member was cached.
            if (reference.Parent.Kind == HandleKind.TypeSpecification)
                metadata.GetType(reference.Parent);
            if (reference.GetKind() == MemberReferenceKind.Field)
            {
                if (!IsGeneratedName(reader.GetString(reference.Name)))
                    metadata.GetType(reference.Parent);
                return;
            }
        }

        if (handle.Kind == HandleKind.FieldDefinition)
        {
            var field = reader.GetFieldDefinition((FieldDefinitionHandle)handle);
            if (!IsGeneratedName(reader.GetString(field.Name)))
                metadata.GetType(field.GetDeclaringType());
            return;
        }

        var member = GetMember(handle);
        if (member is null)
            return;
        if (IsGeneratedName(member.Name) || IsGenerated(member.DeclaringType))
        {
            var generated = ResolveGeneratedMethod(handle, member);
            if (!generated.IsNil)
                ReadBody(generated);
            return;
        }

        references.Add(member.DeclaringType);
        calls.Add(member);
    }

    private MethodDefinitionHandle ResolveGeneratedMethod(EntityHandle handle, DependencyMember member)
    {
        if (handle.Kind == HandleKind.MethodDefinition)
            return (MethodDefinitionHandle)handle;
        if (member.DeclaringType.AssemblyName != metadata.AssemblyName)
            return default;
        var declaring = metadata.FindDefinition(member.DeclaringType.FullName);
        if (declaring.IsNil)
            return default;

        var reader = metadata.Reader;
        var reference = reader.GetMemberReference((MemberReferenceHandle)handle);
        var signature = reader.GetBlobReader(reference.Signature);
        var header = signature.ReadSignatureHeader();
        var arity = header.IsGeneric ? signature.ReadCompressedInteger() : 0;
        var parameterCount = signature.ReadCompressedInteger();
        foreach (var candidate in reader.GetTypeDefinition(declaring).GetMethods())
        {
            var method = reader.GetMethodDefinition(candidate);
            if (!reader.StringComparer.Equals(method.Name, member.Name))
                continue;
            var definition = reader.GetBlobReader(method.Signature);
            var definitionHeader = definition.ReadSignatureHeader();
            var definitionArity = definitionHeader.IsGeneric ? definition.ReadCompressedInteger() : 0;
            if (definitionArity == arity && definition.ReadCompressedInteger() == parameterCount)
                return candidate;
        }
        return default;
    }

    private DependencyMember? GetMember(EntityHandle handle)
    {
        if (members.TryGetValue(handle, out var cached))
            return cached;
        var reader = metadata.Reader;
        DependencyType? type;
        StringHandle name;
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                type = metadata.Resolve(method.GetDeclaringType());
                name = method.Name;
                break;
            case HandleKind.MemberReference:
                var reference = reader.GetMemberReference((MemberReferenceHandle)handle);
                type = reference.Parent.Kind == HandleKind.MethodDefinition
                    ? metadata.Resolve(reader.GetMethodDefinition((MethodDefinitionHandle)reference.Parent).GetDeclaringType())
                    : metadata.Resolve(reference.Parent);
                name = reference.Name;
                break;
            default:
                return null;
        }

        if (type is null)
            return null;
        var member = new DependencyMember(type, reader.GetString(name), MetadataTokens.GetToken(handle));
        members.Add(handle, member);
        return member;
    }

    private static bool IsGeneratedName(string name) => name.StartsWith('<') || name.StartsWith('!');

    private static bool IsGenerated(DependencyType type) =>
        IsGeneratedName(type.Name) || type.FullName.Contains("+<", StringComparison.Ordinal)
            || type.FullName.Contains("+!", StringComparison.Ordinal);
}
