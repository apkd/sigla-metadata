using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Sigla.Metadata;

// The output describes metadata, never executable IL. Target assemblies are not loaded.
public static class AssemblyExtractor
{
    public static byte[]? Extract(byte[] bytes, string filename)
    {
        if (bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z') return null;
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return null;
        var reader = pe.GetMetadataReader();
        var types = new Signatures(reader, filename);
        string Text(StringHandle h) => reader.GetString(h);
        string Blob(BlobHandle h) => Convert.ToHexStringLower(reader.GetBlobBytes(h));
        object? Constant(ConstantHandle handle)
        {
            if (handle.IsNil) return null;
            var value = reader.GetConstant(handle);
            return new { type = value.TypeCode.ToString(), bytes = Blob(value.Value) };
        }
        object Method(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.MethodDefinition => MethodDefinition((MethodDefinitionHandle)handle),
            HandleKind.MemberReference => MemberReference((MemberReferenceHandle)handle),
            _ => throw new BadImageFormatException($"Invalid method reference: {handle.Kind}")
        };
        object MethodDefinition(MethodDefinitionHandle handle)
        {
            var m = reader.GetMethodDefinition(handle);
            return new { owner = types.Entity(m.GetDeclaringType()), name = Text(m.Name), signature = Signature(m.DecodeSignature(types, null)) };
        }
        object MemberReference(MemberReferenceHandle handle)
        {
            var m = reader.GetMemberReference(handle);
            return new { owner = types.Entity(m.Parent), name = Text(m.Name), signature = Signature(m.DecodeMethodSignature(types, null)) };
        }
        object[] Attributes(CustomAttributeHandleCollection handles) => handles.Select(h =>
        {
            var a = reader.GetCustomAttribute(h);
            // Preserve the ECMA-335 value blob: external enum argument types require
            // other assemblies to decode. Constructor signatures retain that context.
            return (object)new { constructor = Method(a.Constructor), value = Blob(a.Value) };
        }).ToArray();
        object[] Generics(GenericParameterHandleCollection handles) => handles.Select(h =>
        {
            var p = reader.GetGenericParameter(h);
            return (object)new { name = Text(p.Name), index = p.Index, flags = (int)p.Attributes,
                constraints = p.GetConstraints().Select(c => types.Entity(reader.GetGenericParameterConstraint(c).Type)).ToArray(),
                attributes = Attributes(p.GetCustomAttributes()) };
        }).ToArray();
        object AssemblyIdentity(AssemblyReference a) => new { name = Text(a.Name), version = a.Version.ToString(), culture = Text(a.Culture), flags = (int)a.Flags, publicKeyOrToken = Blob(a.PublicKeyOrToken), hash = Blob(a.HashValue) };
        var assembly = reader.IsAssembly ? reader.GetAssemblyDefinition() : default;
        var definitions = reader.TypeDefinitions.Select(h =>
        {
            var t = reader.GetTypeDefinition(h);
            return new
            {
                id = MetadataTokens.GetToken(h), type = types.Entity(h), flags = (int)t.Attributes,
                baseType = t.BaseType.IsNil ? null : types.Entity(t.BaseType),
                generics = Generics(t.GetGenericParameters()), attributes = Attributes(t.GetCustomAttributes()),
                interfaces = t.GetInterfaceImplementations().Select(i => { var v = reader.GetInterfaceImplementation(i); return new { type = types.Entity(v.Interface), attributes = Attributes(v.GetCustomAttributes()) }; }).ToArray(),
                implementations = t.GetMethodImplementations().Select(i => { var v = reader.GetMethodImplementation(i); return new { body = Method(v.MethodBody), declaration = Method(v.MethodDeclaration) }; }).ToArray(),
                fields = t.GetFields().Select(f => { var v = reader.GetFieldDefinition(f); return new { id = MetadataTokens.GetToken(f), name = Text(v.Name), flags = (int)v.Attributes, type = v.DecodeSignature(types, null), constant = Constant(v.GetDefaultValue()), attributes = Attributes(v.GetCustomAttributes()), marshal = Blob(v.GetMarshallingDescriptor()), offset = v.GetOffset() }; }).ToArray(),
                methods = t.GetMethods().Select(m =>
                {
                    var v = reader.GetMethodDefinition(m);
                    return new { id = MetadataTokens.GetToken(m), name = Text(v.Name), flags = (int)v.Attributes, implementationFlags = (int)v.ImplAttributes,
                        signature = Signature(v.DecodeSignature(types, null)), generics = Generics(v.GetGenericParameters()), attributes = Attributes(v.GetCustomAttributes()),
                        parameters = v.GetParameters().Select(p => { var x = reader.GetParameter(p); return new { name = Text(x.Name), sequence = x.SequenceNumber, flags = (int)x.Attributes, constant = Constant(x.GetDefaultValue()), attributes = Attributes(x.GetCustomAttributes()), marshal = Blob(x.GetMarshallingDescriptor()) }; }).ToArray() };
                }).ToArray(),
                properties = t.GetProperties().Select(p => { var v = reader.GetPropertyDefinition(p); var a = v.GetAccessors(); return new { name = Text(v.Name), flags = (int)v.Attributes, signature = Signature(v.DecodeSignature(types, null)), constant = Constant(v.GetDefaultValue()), attributes = Attributes(v.GetCustomAttributes()), getter = Token(a.Getter), setter = Token(a.Setter), others = a.Others.Select(Token).ToArray() }; }).ToArray(),
                events = t.GetEvents().Select(e => { var v = reader.GetEventDefinition(e); var a = v.GetAccessors(); return new { name = Text(v.Name), flags = (int)v.Attributes, type = types.Entity(v.Type), attributes = Attributes(v.GetCustomAttributes()), adder = Token(a.Adder), remover = Token(a.Remover), raiser = Token(a.Raiser), others = a.Others.Select(Token).ToArray() }; }).ToArray(),
                layout = new { size = t.GetLayout().Size, packing = t.GetLayout().PackingSize }
            };
        }).ToArray();
        object? identity = reader.IsAssembly ? new { name = Text(assembly.Name), version = assembly.Version.ToString(), culture = Text(assembly.Culture), flags = (int)assembly.Flags, publicKey = Blob(assembly.PublicKey), attributes = Attributes(assembly.GetCustomAttributes()) } : null;
        object ExportTarget(EntityHandle h) => h.Kind switch
        {
            HandleKind.AssemblyReference => new { kind = "assembly", identity = AssemblyIdentity(reader.GetAssemblyReference((AssemblyReferenceHandle)h)) },
            HandleKind.ExportedType => new { kind = "nested", token = MetadataTokens.GetToken(h) },
            HandleKind.AssemblyFile => new { kind = "file", name = Text(reader.GetAssemblyFile((AssemblyFileHandle)h).Name) },
            _ => throw new BadImageFormatException($"Invalid exported type target: {h.Kind}")
        };
        return Data.Encode(new
        {
            format = Data.Analysis, kind = "assembly", identity, module = Text(reader.GetModuleDefinition().Name),
            references = reader.AssemblyReferences.Select(h => AssemblyIdentity(reader.GetAssemblyReference(h))).ToArray(),
            types = definitions,
            exportedTypes = reader.ExportedTypes.Select(h => { var v = reader.GetExportedType(h); return new { id = MetadataTokens.GetToken(h), name = Text(v.Name), ns = Text(v.Namespace), flags = (int)v.Attributes, forwarder = v.IsForwarder, target = ExportTarget(v.Implementation) }; }).ToArray()
        });
    }

    static int? Token(MethodDefinitionHandle h) => h.IsNil ? null : MetadataTokens.GetToken(h);
    static object Signature(MethodSignature<TypeSignature> s) => new { header = s.Header.RawValue, genericCount = s.GenericParameterCount, requiredParameters = s.RequiredParameterCount, returns = s.ReturnType, parameters = s.ParameterTypes };

    public sealed record TypeSignature(string Kind, string? Name = null, string? Assembly = null,
        TypeSignature? Element = null, ImmutableArray<TypeSignature>? Arguments = null,
        int? Index = null, int? Rank = null, ImmutableArray<int>? Sizes = null, ImmutableArray<int>? LowerBounds = null,
        TypeSignature? Modifier = null, bool? Required = null, object? Signature = null);

    sealed class Signatures(MetadataReader reader, string filename) : ISignatureTypeProvider<TypeSignature, object?>
    {
        string Text(StringHandle h) => reader.GetString(h);
        string Current => reader.IsAssembly ? Text(reader.GetAssemblyDefinition().Name) : filename;
        public TypeSignature Entity(EntityHandle h) => h.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeFromDefinition(reader, (TypeDefinitionHandle)h, 0),
            HandleKind.TypeReference => GetTypeFromReference(reader, (TypeReferenceHandle)h, 0),
            HandleKind.TypeSpecification => GetTypeFromSpecification(reader, null, (TypeSpecificationHandle)h, 0),
            HandleKind.ModuleReference => new("module", Text(reader.GetModuleReference((ModuleReferenceHandle)h).Name)),
            HandleKind.MethodDefinition => Entity(reader.GetMethodDefinition((MethodDefinitionHandle)h).GetDeclaringType()),
            _ => throw new BadImageFormatException($"Invalid type reference: {h.Kind}")
        };
        public TypeSignature GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte rawTypeKind)
        {
            var t = r.GetTypeDefinition(h);
            var parent = t.GetDeclaringType();
            var name = parent.IsNil ? Qualify(Text(t.Namespace), Text(t.Name)) : Entity(parent).Name + "+" + Text(t.Name);
            return new("named", name, Current);
        }
        public TypeSignature GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte rawTypeKind)
        {
            var t = r.GetTypeReference(h);
            if (t.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                var parent = Entity(t.ResolutionScope);
                return new("named", parent.Name + "+" + Text(t.Name), parent.Assembly);
            }
            var assembly = t.ResolutionScope.Kind == HandleKind.AssemblyReference
                ? Text(r.GetAssemblyReference((AssemblyReferenceHandle)t.ResolutionScope).Name) : Current;
            return new("named", Qualify(Text(t.Namespace), Text(t.Name)), assembly);
        }
        static string Qualify(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
        public TypeSignature GetTypeFromSpecification(MetadataReader r, object? context, TypeSpecificationHandle h, byte rawTypeKind) => r.GetTypeSpecification(h).DecodeSignature(this, context);
        public TypeSignature GetArrayType(TypeSignature elementType, ArrayShape shape) => new("array", Element: elementType, Rank: shape.Rank, Sizes: shape.Sizes, LowerBounds: shape.LowerBounds);
        public TypeSignature GetByReferenceType(TypeSignature elementType) => new("byref", Element: elementType);
        public TypeSignature GetFunctionPointerType(MethodSignature<TypeSignature> signature) => new("function", Signature: AssemblyExtractor.Signature(signature));
        public TypeSignature GetGenericInstantiation(TypeSignature genericType, ImmutableArray<TypeSignature> typeArguments) => new("generic", Element: genericType, Arguments: typeArguments);
        public TypeSignature GetGenericMethodParameter(object? context, int index) => new("methodParameter", Index: index);
        public TypeSignature GetGenericTypeParameter(object? context, int index) => new("typeParameter", Index: index);
        public TypeSignature GetModifiedType(TypeSignature modifier, TypeSignature unmodifiedType, bool isRequired) => new("modified", Element: unmodifiedType, Modifier: modifier, Required: isRequired);
        public TypeSignature GetPinnedType(TypeSignature elementType) => new("pinned", Element: elementType);
        public TypeSignature GetPointerType(TypeSignature elementType) => new("pointer", Element: elementType);
        public TypeSignature GetPrimitiveType(PrimitiveTypeCode typeCode) => new("primitive", typeCode.ToString());
        public TypeSignature GetSZArrayType(TypeSignature elementType) => new("vector", Element: elementType);
    }
}
