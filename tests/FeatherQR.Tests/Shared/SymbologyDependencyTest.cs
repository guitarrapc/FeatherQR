using System.Collections.Immutable;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace FeatherQR.Tests;

/// <summary>
/// The dependency rule of the internals (qrcode-symbologies.md, Internal organization): shared code never references a
/// symbology namespace, and symbology namespaces never reference each other. What two symbologies both need moves to
/// the shared namespaces instead.
/// </summary>
/// <remarks>
/// Two checks. The compiled one reads the library's build for every target framework, so code under a framework's <c>#if</c>
/// is read in the build that compiles it; code under a configuration's (<c>#if DEBUG</c>, and a call to a
/// <c>[Conditional("DEBUG")]</c> method such as <c>Debug.Assert</c>, arguments and all) is read only in the configuration the
/// tests run in, Release on CI. A build older than a source of the library is not read: its case is skipped, or fails on CI,
/// which builds the solution first. It holds whatever the source spelled: a <c>using</c> of any kind, a global import, a
/// linked file. It reads each reference from a type of an internal namespace to a type of another: base types and interfaces,
/// the signatures of fields, methods and locals, catch types, generic constraints, attributes and the types their arguments
/// name (a string argument that spells a type's full name counts too), and the types, members and signatures each method's
/// code names. The probes (SymbologyDependencyProbes.cs) hold each kind to the reader, but a pinned local and a call through a
/// function pointer, which only unsafe code makes.
/// What compiles to no reference gets past it: a constant, an enum member among them, whose value the compiler copies into the
/// code that uses it, and <c>nameof</c>. So the source check reads each internal file's code, comments and <c>#if false</c>
/// branches removed, for the usual spellings of another symbology: its namespace written out, a <c>using</c> of it, or a type
/// qualified by its name. That one reads text, and a spelling it does not know gets past it.
/// </remarks>
public class SymbologyDependencyTest
{
    private static readonly string[] Symbologies = ["StandardQR", "MicroQR", "RmQR"];

    /// <summary>
    /// Every target framework's build: code under <c>#if</c> is compiled into some of them only. A build older than a source of the
    /// library is not read (<see cref="CoreAssemblyDependencyTest.SkipIfStale"/>).
    /// </summary>
    [Test]
    [MethodDataSource(typeof(CoreAssemblyDependencyTest), nameof(CoreAssemblyDependencyTest.CoreTargetFrameworks))]
    public async Task BuiltLibrary_NoInternalTypeReferencesAnotherSymbology(string targetFramework)
    {
        var path = CoreAssemblyDependencyTest.FindCoreBuild(targetFramework);
        await Assert.That(path).IsNotNull()
            .Because($"no FeatherQR.dll for {targetFramework} under src/FeatherQR/bin; build the solution (dotnet build) before running the tests");
        CoreAssemblyDependencyTest.SkipIfStale(path!);

        var references = CompiledReferences(path!, "FeatherQR.Internals");
        var known = new Reference("QRMatrixDecoder", "FeatherQR.Internals.StandardQR", "EccBlockDecoder", "FeatherQR.Internals.BinaryDecoders");
        await Assert.That(references.Contains(known)).IsTrue().Because($"{path} holds {known}, as every build does");

        var violations = references
            .Where(r => IsViolation(r.FromNamespace, r.ToNamespace))
            .Select(r => $"{r.From} ({r.FromNamespace}) -> {r.To} ({r.ToNamespace})")
            .ToArray();
        await Assert.That(violations).IsEmpty().Because($"{path}: {string.Join("; ", violations)}");
    }

    [Test]
    [Arguments("FeatherQR.Internals.MicroQR", "FeatherQR.Internals.StandardQR", true)]
    [Arguments("FeatherQR.Internals.ImageDecoders", "FeatherQR.Internals.RmQR", true)]
    [Arguments("FeatherQR.Internals", "FeatherQR.Internals.MicroQR", true)]
    [Arguments("FeatherQR.Internals.MicroQR", "FeatherQR.Internals.StandardQR.Sub", true)]
    [Arguments("FeatherQR.Internals.StandardQRLike", "FeatherQR.Internals.StandardQR", true)]
    [Arguments("FeatherQR.Internals.StandardQR", "FeatherQR.Internals.BinaryDecoders", false)]
    [Arguments("FeatherQR.Internals.StandardQR", "FeatherQR", false)]
    [Arguments("FeatherQR.Internals.StandardQR.Sub", "FeatherQR.Internals.StandardQR", false)]
    [Arguments("FeatherQR.Internals.StandardQR", "FeatherQR.Internals.StandardQR.Sub", false)]
    [Arguments("FeatherQR.Internals.MicroQR", "FeatherQR.Internals.StandardQRLike", false)]
    public async Task IsViolation_IsAReferenceIntoASymbologyFromOutsideIt(string fromNamespace, string toNamespace, bool expected)
    {
        await Assert.That(IsViolation(fromNamespace, toNamespace)).IsEqualTo(expected);
    }

    /// <summary>
    /// The compiled check reads each kind of reference: each probe names one target, in one place of the metadata
    /// (SymbologyDependencyProbes.cs), so a kind the reader stops reading fails its case; a pinned local and a call through a function pointer have no probe.
    /// </summary>
    [Test]
    [Arguments("BaseProbe", "BaseTarget")]
    [Arguments("InterfaceProbe", "IInterfaceTarget")]
    [Arguments("FieldProbe", "FieldTarget")]
    [Arguments("ParameterProbe", "ParameterTarget")]
    [Arguments("ReturnProbe", "ReturnTarget")]
    [Arguments("ArrayProbe", "ArrayTarget")]
    [Arguments("MultiDimensionalArrayProbe", "MultiDimensionalArrayTarget")]
    [Arguments("ByReferenceProbe", "ByReferenceTarget")]
    [Arguments("IModifiedProbe", "ModifiedTarget")]
    [Arguments("PointerProbe", "PointerTarget")]
    [Arguments("FunctionPointerProbe", "FunctionPointerTarget")]
    [Arguments("FunctionPointerProbe", "FunctionPointerReturnTarget")]
    [Arguments("LocalProbe", "LocalTarget")]
    [Arguments("CatchProbe", "CatchTarget")]
    [Arguments("TypeTokenProbe", "TypeTokenTarget")]
    [Arguments("CastProbe", "CastTarget")]
    [Arguments("LambdaProbe", "LambdaTarget")]
    [Arguments("NestedTypeProbe", "NestedTypeTarget")]
    [Arguments("FieldTokenProbe", "FieldTokenTarget")]
    [Arguments("MethodTokenProbe", "MethodTokenTarget")]
    [Arguments("BridgedMethodProbe", "BridgedMethodTarget")]
    [Arguments("BridgedFieldProbe", "BridgedFieldTarget")]
    [Arguments("GenericTypeProbe", "GenericTypeTarget`1")]
    [Arguments("GenericArgumentProbe", "GenericArgumentTarget")]
    [Arguments("MethodArgumentProbe", "MethodArgumentTarget")]
    [Arguments("GenericMethodProbe", "GenericMethodTarget")]
    [Arguments("MemberSignatureProbe", "MemberSignatureTarget")]
    [Arguments("MemberFieldSignatureProbe", "FieldSignatureTarget")]
    [Arguments("TypeAttributeProbe", "TypeAttributeTarget")]
    [Arguments("MethodAttributeProbe", "MethodAttributeTarget")]
    [Arguments("FieldAttributeProbe", "FieldAttributeTarget")]
    [Arguments("ParameterAttributeProbe", "ParameterAttributeTarget")]
    [Arguments("PropertyAttributeProbe", "PropertyAttributeTarget")]
    [Arguments("EventAttributeProbe", "EventAttributeTarget")]
    [Arguments("GenericParameterAttributeProbe`1", "GenericParameterAttributeTarget")]
    [Arguments("AttributeArgumentProbe", "AttributeArgumentTarget")]
    [Arguments("AttributeGenericArgumentProbe", "AttributeGenericArgumentTarget")]
    [Arguments("AttributeNestedProbe", "AttributeNestedOwnerTarget")]
    [Arguments("AttributePointerProbe", "AttributePointerTarget")]
    [Arguments("AttributeGenericArgumentsProbe", "AttributeSecondArgumentTarget")]
    [Arguments("AttributeOwnGenericProbe", "AttributeGenericOwnerTarget`1")]
    [Arguments("AttributeLongNameProbe", "AttributeLongNameTargetWhoseFullNameRunsPastOneHundredAndTwentySevenBytesSoItsLengthTakesTwoBytes")]
    [Arguments("TypeConstraintProbe`1", "ITypeConstraintTarget")]
    [Arguments("MethodConstraintProbe", "IMethodConstraintTarget")]
    public async Task CompiledReferences_ReadEachKindOfReference(string probe, string target)
    {
        await Assert.That(ProbeReferences.Value.Any(r => r.From == probe && r.To == target)).IsTrue().Because($"{probe} -> {target}");
    }

    private static readonly Lazy<IReadOnlyCollection<Reference>> ProbeReferences
        = new(() => CompiledReferences(typeof(SymbologyDependencyTest).Assembly.Location, "FeatherQR.Tests.DependencyProbes"));

    [Test]
    public async Task NoInternalFile_NamesAnotherSymbology()
    {
        var violations = new List<string>();
        foreach (var (relative, path) in InternalFiles())
        {
            var folder = relative.Contains('/') ? relative.Substring(0, relative.IndexOf('/')) : "";
            foreach (var symbology in Symbologies)
            {
                if (symbology == folder)
                    continue;
                foreach (var line in References(File.ReadAllText(path), symbology))
                    violations.Add($"{relative}: {line}");
            }
        }

        await Assert.That(violations).IsEmpty().Because(string.Join("; ", violations));
    }

    [Test]
    [Arguments("using FeatherQR.Internals.StandardQR;", true)]
    [Arguments("using static FeatherQR.Internals.StandardQR.QRCodeConstants;", true)]
    [Arguments("using Sampler = FeatherQR.Internals.StandardQR.QRImageDecoder;", true)]
    [Arguments("using FeatherQR.Internals.ImageDecoders; using FeatherQR.Internals.StandardQR;", true)]
    [Arguments("global using global::FeatherQR.Internals.StandardQR;", true)]
    [Arguments("using StandardQR;", true)]
    [Arguments("using Internals.StandardQR;", true)]
    [Arguments("        FeatherQR.Internals.StandardQR.QRImageDecoder.SampleGrid(luminance);", true)]
    [Arguments("        StandardQR.QRImageDecoder.SampleGrid(luminance);", true)]
    [Arguments("        Internals.StandardQR.QRCodeConstants.GetRemainderBits(1);", true)]
    [Arguments("/// <see cref=\"StandardQR.QRImageDecoder\"/>", false)]
    [Arguments("        // as StandardQR.QRImageDecoder does", false)]
    [Arguments("/* StandardQR.QRImageDecoder,\n   over two lines */", false)]
    [Arguments("#if false\n        StandardQR.QRImageDecoder.SampleGrid(luminance);\n#endif", false)]
    [Arguments("#if false\n        Old();\n#else\n        StandardQR.QRImageDecoder.SampleGrid(luminance);\n#endif", true)]
    [Arguments("#if false\n        Old();\n#elif NETSTANDARD2_0\n        StandardQR.QRImageDecoder.SampleGrid(luminance);\n#endif", true)]
    [Arguments("#if false\n#if DEBUG\n        Old();\n#endif\n        StandardQR.QRImageDecoder.SampleGrid(luminance);\n#endif", false)]
    [Arguments("#if false || DEBUG\n        StandardQR.QRImageDecoder.SampleGrid(luminance);\n#endif", true)]
    [Arguments("#if false\n        Old();\n# endif\n        StandardQR.QRImageDecoder.SampleGrid(luminance);", true)]
    [Arguments("# if false // retired\r\n        StandardQR.QRImageDecoder.SampleGrid(luminance);\r\n#endif\r\n", false)]
    [Arguments("using FeatherQR.Internals.ImageDecoders;", false)]
    [Arguments("        var x = MyStandardQR.Value;", false)]
    public async Task References_AreTheUsualSpellings_OutsideComments(string code, bool expected)
    {
        await Assert.That(References(code, "StandardQR").Any()).IsEqualTo(expected);
    }

    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline);

    // A directive may have spaces after its '#'; only a condition of false alone, a comment after it, is dead code
    private static readonly Regex IfFalse = new(@"^#\s*if\s+false\s*(//.*)?$");

    private static readonly Regex Directive = new(@"^#\s*(if|elif|else|endif)\b");

    /// <summary>
    /// The text with each <c>#if false</c> branch blanked, up to its own <c>#else</c>, <c>#elif</c> or <c>#endif</c>: the
    /// directives nested in it are counted, and the branch after its <c>#else</c> or <c>#elif</c> is kept.
    /// </summary>
    private static string WithoutIfFalse(string text)
    {
        var lines = text.Split('\n');
        var depth = 0; // the #if nesting inside a #if false branch, 0 outside one
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (depth == 0)
            {
                if (IfFalse.IsMatch(line))
                {
                    depth = 1;
                    lines[i] = "";
                }
                continue;
            }
            switch (Directive.Match(line) is { Success: true } directive ? directive.Groups[1].Value : "")
            {
                case "if":
                    depth++;
                    break;
                case "endif":
                    depth--;
                    break;
                case "else" or "elif" when depth == 1:
                    depth = 0;
                    break;
            }
            lines[i] = "";
        }
        return string.Join("\n", lines);
    }

    private static IEnumerable<string> References(string text, string symbology)
    {
        // The namespace written out, a using of it however written, or a type qualified by the symbology's name
        var fullName = new Regex($@"FeatherQR\.Internals\.{symbology}\b");
        var usingDirective = new Regex($@"\busing\s+(static\s+)?(\w+\s*=\s*)?(global::)?((FeatherQR\.)?Internals\.)?{symbology}\b");
        var qualifiedName = new Regex($@"(?<![\w.])(Internals\.)?{symbology}\.[A-Z]");
        var code = WithoutIfFalse(BlockComment.Replace(text, " "));
        foreach (var line in code.Split('\n'))
        {
            var withoutComment = line;
            var comment = withoutComment.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
                withoutComment = withoutComment.Substring(0, comment);
            if (fullName.IsMatch(withoutComment) || usingDirective.IsMatch(withoutComment) || qualifiedName.IsMatch(withoutComment))
                yield return line.Trim();
        }
    }

    /// <summary>
    /// A reference into a symbology from outside it, from shared code or from another symbology. A namespace below a symbology's
    /// is part of it, as its folder is to the source check.
    /// </summary>
    private static bool IsViolation(string fromNamespace, string toNamespace)
        => SymbologyOf(toNamespace) is { } to && to != SymbologyOf(fromNamespace);

    private static string? SymbologyOf(string ns)
    {
        foreach (var symbology in Symbologies)
        {
            var root = "FeatherQR.Internals." + symbology;
            if (ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal))
                return symbology;
        }
        return null;
    }

    private readonly record struct Reference(string From, string FromNamespace, string To, string ToNamespace);

    /// <summary>
    /// Every reference in an assembly from a type in <paramref name="fromNamespace"/> or below it to a type in another namespace
    /// of that assembly, by the top-level types' names.
    /// </summary>
    private static IReadOnlyCollection<Reference> CompiledReferences(string path, string fromNamespace)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var collector = new DefinitionCollector();
        var names = TypeNames(reader);
        var references = new HashSet<Reference>();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var from = TopLevel(reader, typeHandle);
            if (!from.Namespace.StartsWith(fromNamespace, StringComparison.Ordinal))
                continue;

            var targets = new List<TypeDefinitionHandle>();
            var type = reader.GetTypeDefinition(typeHandle);
            AddHandle(reader, collector, targets, type.BaseType);
            foreach (var implementation in type.GetInterfaceImplementations())
                AddHandle(reader, collector, targets, reader.GetInterfaceImplementation(implementation).Interface);
            AddAttributes(reader, collector, names, targets, type.GetCustomAttributes());
            AddGenericParameters(reader, collector, names, targets, type.GetGenericParameters());
            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                targets.AddRange(field.DecodeSignature(collector, null));
                AddAttributes(reader, collector, names, targets, field.GetCustomAttributes());
            }
            // A property's and an event's types are in their accessors' signatures
            foreach (var property in type.GetProperties())
                AddAttributes(reader, collector, names, targets, reader.GetPropertyDefinition(property).GetCustomAttributes());
            foreach (var @event in type.GetEvents())
                AddAttributes(reader, collector, names, targets, reader.GetEventDefinition(@event).GetCustomAttributes());
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                AddSignature(targets, method.DecodeSignature(collector, null));
                AddAttributes(reader, collector, names, targets, method.GetCustomAttributes());
                AddGenericParameters(reader, collector, names, targets, method.GetGenericParameters());
                foreach (var parameter in method.GetParameters())
                    AddAttributes(reader, collector, names, targets, reader.GetParameter(parameter).GetCustomAttributes());
                if (method.RelativeVirtualAddress != 0)
                    AddBody(reader, collector, targets, pe.GetMethodBody(method.RelativeVirtualAddress));
            }

            foreach (var target in targets)
            {
                var to = TopLevel(reader, target);
                if (to.Namespace != from.Namespace)
                    references.Add(new Reference(from.Name, from.Namespace, to.Name, to.Namespace));
            }
        }
        return references;
    }

    private static readonly Dictionary<ushort, OperandType> OperandTypes = typeof(OpCodes).GetFields()
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => (ushort)opCode.Value, opCode => opCode.OperandType);

    private static void AddBody(MetadataReader reader, DefinitionCollector collector, List<TypeDefinitionHandle> targets, MethodBodyBlock body)
    {
        if (!body.LocalSignature.IsNil)
        {
            foreach (var local in reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(collector, null))
                targets.AddRange(local);
        }
        foreach (var region in body.ExceptionRegions)
            AddHandle(reader, collector, targets, region.CatchType);

        var il = body.GetILReader();
        while (il.RemainingBytes > 0)
        {
            ushort code = il.ReadByte();
            if (code == 0xFE)
                code = (ushort)(0xFE00 | il.ReadByte());
            switch (OperandTypes[code])
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                    il.Offset += 1;
                    break;
                case OperandType.InlineVar:
                    il.Offset += 2;
                    break;
                case OperandType.InlineI8 or OperandType.InlineR:
                    il.Offset += 8;
                    break;
                case OperandType.InlineSwitch:
                    var cases = il.ReadInt32();
                    il.Offset += 4 * cases;
                    break;
                // InlineSig is a calli's signature, the one place a pointer cast from an integer names its types
                case OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineTok or OperandType.InlineType or OperandType.InlineSig:
                    AddHandle(reader, collector, targets, MetadataTokens.EntityHandle(il.ReadInt32()));
                    break;
                default:
                    il.Offset += 4;
                    break;
            }
        }
    }

    private static void AddHandle(MetadataReader reader, DefinitionCollector collector, List<TypeDefinitionHandle> targets, EntityHandle handle)
    {
        if (handle.IsNil)
            return;
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                targets.Add((TypeDefinitionHandle)handle);
                break;
            case HandleKind.TypeSpecification:
                targets.AddRange(reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(collector, null));
                break;
            // A member the assembly defines, with its signature: its declaring type's own signatures are read only when that type
            // is in the namespaces read, and one outside them (the library's root namespace) is not
            case HandleKind.MethodDefinition:
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                targets.Add(method.GetDeclaringType());
                AddSignature(targets, method.DecodeSignature(collector, null));
                break;
            case HandleKind.FieldDefinition:
                var field = reader.GetFieldDefinition((FieldDefinitionHandle)handle);
                targets.Add(field.GetDeclaringType());
                targets.AddRange(field.DecodeSignature(collector, null));
                break;
            case HandleKind.MemberReference:
                var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                AddHandle(reader, collector, targets, member.Parent);
                if (member.GetKind() == MemberReferenceKind.Method)
                    AddSignature(targets, member.DecodeMethodSignature(collector, null));
                else
                    targets.AddRange(member.DecodeFieldSignature(collector, null));
                break;
            case HandleKind.MethodSpecification:
                var specification = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                AddHandle(reader, collector, targets, specification.Method);
                foreach (var argument in specification.DecodeSignature(collector, null))
                    targets.AddRange(argument);
                break;
            case HandleKind.StandaloneSignature:
                AddSignature(targets, reader.GetStandaloneSignature((StandaloneSignatureHandle)handle).DecodeMethodSignature(collector, null));
                break;
        }
    }

    private static void AddGenericParameters(MetadataReader reader, DefinitionCollector collector, Dictionary<string, TypeDefinitionHandle> names, List<TypeDefinitionHandle> targets, GenericParameterHandleCollection parameters)
    {
        foreach (var handle in parameters)
        {
            var parameter = reader.GetGenericParameter(handle);
            AddAttributes(reader, collector, names, targets, parameter.GetCustomAttributes());
            foreach (var constraint in parameter.GetConstraints())
                AddHandle(reader, collector, targets, reader.GetGenericParameterConstraint(constraint).Type);
        }
    }

    /// <summary>
    /// Each attribute's type, through its constructor, and the assembly's types its arguments name: a <see cref="Type"/> argument
    /// is stored as the type's name, a string of its own in the attribute's value.
    /// </summary>
    private static void AddAttributes(MetadataReader reader, DefinitionCollector collector, Dictionary<string, TypeDefinitionHandle> names, List<TypeDefinitionHandle> targets, CustomAttributeHandleCollection attributes)
    {
        foreach (var handle in attributes)
        {
            var attribute = reader.GetCustomAttribute(handle);
            AddHandle(reader, collector, targets, attribute.Constructor);
            var value = reader.GetBlobBytes(attribute.Value);
            for (var start = 0; start < value.Length; start++)
            {
                // Each place a string could start: its length, compressed to one byte below 0x80 or two below 0x4000, then its UTF-8 bytes
                var (length, text) = value[start] < 0x80 ? (value[start], start + 1)
                    : (value[start] & 0xC0) == 0x80 && start + 1 < value.Length ? (((value[start] & 0x3F) << 8) | value[start + 1], start + 2)
                    : (0, 0);
                if (length == 0 || text + length > value.Length)
                    continue;
                // A generic type's name carries its arguments' names in brackets, separated by commas, an argument of another
                // assembly in brackets of its own with its assembly's name; an array's or a pointer's name is its element's
                // followed by brackets or a star
                foreach (var part in Encoding.UTF8.GetString(value, text, length).Split('[', ']', ',', '*'))
                {
                    if (names.TryGetValue(part.Trim(), out var named))
                        targets.Add(named);
                }
            }
        }
    }

    /// <summary>The assembly's types by the name an attribute argument stores: a nested type's after its declaring type's and a '+'.</summary>
    private static Dictionary<string, TypeDefinitionHandle> TypeNames(MetadataReader reader)
    {
        var names = new Dictionary<string, TypeDefinitionHandle>();
        foreach (var handle in reader.TypeDefinitions)
            names[TypeName(reader, handle)] = handle;
        return names;
    }

    private static string TypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        if (type.IsNested)
            return TypeName(reader, type.GetDeclaringType()) + "+" + name;
        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private static void AddSignature(List<TypeDefinitionHandle> targets, MethodSignature<ImmutableArray<TypeDefinitionHandle>> signature)
    {
        targets.AddRange(signature.ReturnType);
        foreach (var parameter in signature.ParameterTypes)
            targets.AddRange(parameter);
    }

    private static (string Name, string Namespace) TopLevel(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        while (type.IsNested)
            type = reader.GetTypeDefinition(type.GetDeclaringType());
        return (reader.GetString(type.Name), reader.GetString(type.Namespace));
    }

    /// <summary>The assembly's own type definitions a signature names; types of other assemblies are not the rule's.</summary>
    private sealed class DefinitionCollector : ISignatureTypeProvider<ImmutableArray<TypeDefinitionHandle>, object?>
    {
        private static readonly ImmutableArray<TypeDefinitionHandle> None = [];

        public ImmutableArray<TypeDefinitionHandle> GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => [handle];
        public ImmutableArray<TypeDefinitionHandle> GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => None;
        public ImmutableArray<TypeDefinitionHandle> GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, context);
        public ImmutableArray<TypeDefinitionHandle> GetPrimitiveType(PrimitiveTypeCode typeCode) => None;
        public ImmutableArray<TypeDefinitionHandle> GetSZArrayType(ImmutableArray<TypeDefinitionHandle> elementType) => elementType;
        public ImmutableArray<TypeDefinitionHandle> GetArrayType(ImmutableArray<TypeDefinitionHandle> elementType, ArrayShape shape) => elementType;
        public ImmutableArray<TypeDefinitionHandle> GetByReferenceType(ImmutableArray<TypeDefinitionHandle> elementType) => elementType;
        public ImmutableArray<TypeDefinitionHandle> GetPointerType(ImmutableArray<TypeDefinitionHandle> elementType) => elementType;
        public ImmutableArray<TypeDefinitionHandle> GetPinnedType(ImmutableArray<TypeDefinitionHandle> elementType) => elementType;
        public ImmutableArray<TypeDefinitionHandle> GetGenericInstantiation(ImmutableArray<TypeDefinitionHandle> genericType, ImmutableArray<ImmutableArray<TypeDefinitionHandle>> typeArguments) => genericType.AddRange(typeArguments.SelectMany(argument => argument));
        public ImmutableArray<TypeDefinitionHandle> GetGenericMethodParameter(object? context, int index) => None;
        public ImmutableArray<TypeDefinitionHandle> GetGenericTypeParameter(object? context, int index) => None;
        public ImmutableArray<TypeDefinitionHandle> GetFunctionPointerType(MethodSignature<ImmutableArray<TypeDefinitionHandle>> signature) => signature.ReturnType.AddRange(signature.ParameterTypes.SelectMany(parameter => parameter));
        public ImmutableArray<TypeDefinitionHandle> GetModifiedType(ImmutableArray<TypeDefinitionHandle> modifier, ImmutableArray<TypeDefinitionHandle> unmodifiedType, bool isRequired) => unmodifiedType;
    }

    /// <summary>Files under src/FeatherQR/Internals, with their path below it.</summary>
    private static IEnumerable<(string Relative, string Path)> InternalFiles()
    {
        var internals = Path.Combine(RepositoryRoot(), "src", "FeatherQR", "Internals");
        foreach (var file in Directory.EnumerateFiles(internals, "*.cs", SearchOption.AllDirectories))
            yield return (Path.GetRelativePath(internals, file).Replace('\\', '/'), file);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Directory.Build.props) not found above " + AppContext.BaseDirectory);
    }
}
