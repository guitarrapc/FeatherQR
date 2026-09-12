#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

// Holds the exported surface to a listing checked into the repository.
//
//   dotnet run tools/check_public_api.cs -- <approved.txt> <assembly.dll>...            accept
//   dotnet run tools/check_public_api.cs -- --check <approved.txt> <assembly.dll>...    verify
//
// Pass every target framework's build of one assembly, with a shell glob. Visibility and
// signatures come from the metadata tables, the same way filter_public_docs reads them, so
// this needs no package reference, never loads the assembly, and reads a netstandard2.0
// build as readily as a net10.0 one. Running on all of them is the point: the surface must
// be the same everywhere, and nothing else checks that.
//
// This is the gate public_api.cs deliberately is not. The two render the same listing from
// the same assembly - public_api.cs through reflection, for a page with documentation and
// source links; this one through metadata, for four target frameworks and a diff. Their
// type and member text is expected to match line for line, which is how each keeps the
// other honest.
//
// What it catches that package validation does not: an addition. Validation compares
// against the released baseline and reports what would break a caller, so a new public
// member passes it silently. Here every change is a diff someone has to accept.

var check = args.Length > 0 && args[0] == "--check";
var rest = args.Skip(check ? 1 : 0).ToArray();

if (rest.Length < 2)
{
    Console.Error.WriteLine("usage: check_public_api [--check] <approved.txt> <assembly.dll>...");
    return 2;
}

var approvedPath = rest[0];
var assemblyPaths = rest.Skip(1).OrderBy(p => p, StringComparer.Ordinal).ToArray();

var renders = new List<(string Framework, string Text)>();
foreach (var path in assemblyPaths)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"No assembly at {path}.");
        return 1;
    }
    renders.Add((FrameworkOf(path), Render(path)));
}

// A build that produced nothing readable must not be allowed to rewrite the listing as empty.
if (renders[0].Text.Length == 0)
{
    Console.Error.WriteLine($"Read no exported surface out of {assemblyPaths[0]}; refusing to touch {approvedPath}.");
    return 1;
}

// Every framework first, before the listing: a surface that differs between them is a defect
// in its own right, and accepting one of them into the file would bury it.
var divergent = renders.Where(r => r.Text != renders[0].Text).ToArray();
if (divergent.Length > 0)
{
    Console.Error.WriteLine($"The exported surface differs between target frameworks. {renders[0].Framework} against:");
    foreach (var (framework, text) in divergent)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"  {framework}");
        WriteDiff(renders[0].Text, text, "    ");
    }
    return 1;
}

var header = new StringBuilder()
    .AppendLine($"// {Path.GetFileNameWithoutExtension(assemblyPaths[0])}")
    .AppendLine($"// {renders[0].Text.Split('\n').Count(l => l.StartsWith("    public", StringComparison.Ordinal) || l.StartsWith("    protected", StringComparison.Ordinal))} exported types")
    .AppendLine($"// {string.Join(", ", renders.Select(r => r.Framework))}")
    .ToString();

var listing = header + renders[0].Text;

if (!check)
{
    // One line ending, whatever wrote it: the file is compared as text on every platform the
    // build matrix runs on, and a listing that flips between them is a diff nobody asked for.
    File.WriteAllText(approvedPath, listing.Replace("\r\n", "\n"));
    Console.WriteLine($"{approvedPath}: written from {renders.Count} target framework(s).");
    return 0;
}

var approved = File.Exists(approvedPath) ? File.ReadAllText(approvedPath).Replace("\r\n", "\n") : null;
if (approved == listing.Replace("\r\n", "\n"))
{
    Console.WriteLine($"{approvedPath}: unchanged across {renders.Count} target framework(s).");
    return 0;
}

Console.Error.WriteLine(approved is null
    ? $"{approvedPath} does not exist. Run without --check to create it."
    : $"{approvedPath} does not match the built assembly:");
Console.Error.WriteLine();
if (approved is not null) WriteDiff(approved, listing.Replace("\r\n", "\n"), "  ");
Console.Error.WriteLine();
Console.Error.WriteLine("Run the same command without --check to accept the new surface, then review the diff.");
return 1;

// The last directory segment is the target framework: bin/Release/net8.0/X.dll.
static string FrameworkOf(string path) => Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))) ?? "?";

// Types own their members, so a diff that groups by type reads the way a reviewer thinks:
// which type changed, and which of its lines. A flat line diff would report a renamed type
// as one deletion and one addition thirty lines apart.
static void WriteDiff(string expected, string actual, string indent)
{
    var before = Blocks(expected);
    var after = Blocks(actual);

    foreach (var name in before.Keys.Concat(after.Keys).Distinct().OrderBy(n => n, StringComparer.Ordinal))
    {
        var hasBefore = before.TryGetValue(name, out var oldLines);
        var hasAfter = after.TryGetValue(name, out var newLines);

        if (hasBefore && !hasAfter) Console.Error.WriteLine($"{indent}- type {name}");
        else if (!hasBefore && hasAfter) Console.Error.WriteLine($"{indent}+ type {name}");
        else if (!oldLines!.SequenceEqual(newLines!))
        {
            Console.Error.WriteLine($"{indent}  {name}");
            foreach (var line in oldLines!.Except(newLines!)) Console.Error.WriteLine($"{indent}    - {line.Trim()}");
            foreach (var line in newLines!.Except(oldLines!)) Console.Error.WriteLine($"{indent}    + {line.Trim()}");
        }
    }

    // A block is one type, keyed by the name a reader would say out loud. Its declaration is
    // kept as the first line of the block, so a change to the declaration alone - a class that
    // became sealed, a base type that moved - is a line in the diff rather than a silent pass.
    static Dictionary<string, List<string>> Blocks(string text)
    {
        var blocks = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var space = "";
        string? current = null;

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("namespace ", StringComparison.Ordinal)) { space = line["namespace ".Length..]; current = null; }
            else if (line.StartsWith("    public ", StringComparison.Ordinal) || line.StartsWith("    protected ", StringComparison.Ordinal))
            {
                current = $"{space}.{DeclaredName(line.Trim())}";
                blocks[current] = [line.Trim()];
            }
            else if (current is not null && line.StartsWith("        ", StringComparison.Ordinal)) blocks[current].Add(line);
            else if (line == "    }") current = null;
        }
        return blocks;

        // "public sealed class CircleModuleShape : ModuleShape" -> "CircleModuleShape".
        static string DeclaredName(string header)
        {
            foreach (var kind in (string[])["class ", "struct ", "enum ", "interface ", "delegate "])
            {
                var at = header.IndexOf(kind, StringComparison.Ordinal);
                if (at < 0) continue;
                var name = header[(at + kind.Length)..];
                var colon = name.IndexOf(" : ", StringComparison.Ordinal);
                return colon < 0 ? name : name[..colon];
            }
            return header;
        }
    }
}

static string Render(string assemblyPath)
{
    using var stream = File.OpenRead(assemblyPath);
    using var peReader = new PEReader(stream);
    var reader = peReader.GetMetadataReader();
    var provider = new SignatureRenderer(reader);

    var types = new List<(string Namespace, string Sort, string Text)>();

    foreach (var handle in reader.TypeDefinitions)
    {
        var type = reader.GetTypeDefinition(handle);
        if (!IsVisibleType(type)) continue;

        var name = BareName(type);
        var space = NamespaceOf(type);
        types.Add((space, $"{space}.{name}", RenderType(type)));
    }

    var sb = new StringBuilder();
    foreach (var group in types.GroupBy(t => t.Namespace).OrderBy(g => g.Key, StringComparer.Ordinal))
    {
        sb.Append('\n').Append("namespace ").Append(group.Key).Append('\n').Append("{\n");
        foreach (var type in group.OrderBy(t => t.Sort, StringComparer.Ordinal)) sb.Append(type.Text);
        sb.Append("}\n");
    }
    return sb.ToString();

    string RenderType(TypeDefinition type)
    {
        var sb = new StringBuilder();
        sb.Append('\n');
        if (HasAttribute(type.GetCustomAttributes(), "ObsoleteAttribute")) sb.Append("    [Obsolete]\n");
        sb.Append("    ").Append(TypeHeader(type)).Append('\n').Append("    {\n");
        foreach (var member in Members(type)) sb.Append("        ").Append(member).Append('\n');
        sb.Append("    }\n");
        return sb.ToString();
    }

    // Nested protected counts as reachable: a consumer gets at those by deriving. Compiler-generated
    // nested types do not: a C# 14 extension block emits public marker types with unspeakable names
    // (<G>$hash, <M>$hash) that no caller can spell; the members themselves are listed on the
    // enclosing class, which is where a caller finds them.
    bool IsVisibleType(TypeDefinition type)
    {
        var visibility = type.Attributes & TypeAttributes.VisibilityMask;
        if (!type.IsNested) return visibility == TypeAttributes.Public;
        if (reader.GetString(type.Name).Contains('<')) return false;
        return visibility is TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem
            && IsVisibleType(reader.GetTypeDefinition(type.GetDeclaringType()));
    }

    // Nested types read as Outer.Inner, and the arity tick a generic carries is not part of
    // the name a caller writes.
    string BareName(TypeDefinition type)
    {
        var name = reader.GetString(type.Name);
        var tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        return type.IsNested ? $"{BareName(reader.GetTypeDefinition(type.GetDeclaringType()))}.{name}" : name;
    }

    // A nested type carries no namespace of its own; it belongs to the one its outermost
    // declaring type is in.
    string NamespaceOf(TypeDefinition type) => type.IsNested
        ? NamespaceOf(reader.GetTypeDefinition(type.GetDeclaringType()))
        : reader.GetString(type.Namespace);

    string TypeHeader(TypeDefinition type)
    {
        var isInterface = (type.Attributes & TypeAttributes.Interface) != 0;
        var baseName = BaseFullName(type);
        var isEnum = baseName == "System.Enum";
        var isValueType = baseName is "System.ValueType" or "System.Enum";
        var isDelegate = baseName is "System.MulticastDelegate" or "System.Delegate";
        var isAbstract = (type.Attributes & TypeAttributes.Abstract) != 0;
        var isSealed = (type.Attributes & TypeAttributes.Sealed) != 0;

        var kind = isEnum ? "enum" : isInterface ? "interface" : isDelegate ? "delegate" : isValueType ? "struct" : "class";

        var visibility = type.Attributes & TypeAttributes.VisibilityMask;
        var modifiers = new List<string>
        {
            visibility is TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem ? "protected" : "public",
        };
        if (isAbstract && isSealed && !isEnum && !isInterface) modifiers.Add("static");
        else if (isAbstract && !isInterface) modifiers.Add("abstract");
        else if (isSealed && !isEnum && !isValueType) modifiers.Add("sealed");
        if (isValueType && !isEnum && HasAttribute(type.GetCustomAttributes(), "IsReadOnlyAttribute")) modifiers.Add("readonly");
        modifiers.Add(kind);

        var header = $"{string.Join(" ", modifiers)} {BareName(type)}{GenericSuffix(type.GetGenericParameters())}";

        var bases = new List<string>();
        if (kind == "class" && !type.BaseType.IsNil && baseName != "System.Object") bases.Add(TypeText(type.BaseType, Context(type)));

        // Interfaces the base type already carries are noise. Only what this type adds is surface.
        var inherited = InheritedInterfaces(type);
        bases.AddRange(Interfaces(type).Where(i => !inherited.Contains(i)).OrderBy(n => n, StringComparer.Ordinal));

        if (isEnum) bases.Add(UnderlyingTypeOfEnum(type));

        return bases.Count > 0 ? $"{header} : {string.Join(", ", bases)}" : header;
    }

    // Namespace-qualified, because the framework bases below are recognised by name. A
    // constructed generic base is a type specification and has no name of its own; null is
    // right for it, since it is never one of those.
    string? BaseFullName(TypeDefinition type) => type.BaseType.Kind switch
    {
        HandleKind.TypeReference => reader.GetTypeReference((TypeReferenceHandle)type.BaseType) is var r
            ? $"{reader.GetString(r.Namespace)}.{reader.GetString(r.Name)}" : null,
        HandleKind.TypeDefinition => reader.GetTypeDefinition((TypeDefinitionHandle)type.BaseType) is var d
            ? $"{reader.GetString(d.Namespace)}.{reader.GetString(d.Name)}" : null,
        _ => null,
    };

    string[] Interfaces(TypeDefinition type) =>
    [
        .. type.GetInterfaceImplementations()
            .Select(h => TypeText(reader.GetInterfaceImplementation(h).Interface, Context(type)))
    ];

    // Only a base class inside this assembly can be walked. An external one is left alone:
    // nothing here derives from a framework type that carries interfaces of its own.
    HashSet<string> InheritedInterfaces(TypeDefinition type)
    {
        var inherited = new HashSet<string>(StringComparer.Ordinal);
        var current = type.BaseType;
        while (current.Kind == HandleKind.TypeDefinition)
        {
            var declaring = reader.GetTypeDefinition((TypeDefinitionHandle)current);
            foreach (var name in Interfaces(declaring)) inherited.Add(name);
            current = declaring.BaseType;
        }
        return inherited;
    }

    // An enum's storage is the type of its one instance field.
    string UnderlyingTypeOfEnum(TypeDefinition type)
    {
        foreach (var handle in type.GetFields())
        {
            var field = reader.GetFieldDefinition(handle);
            if ((field.Attributes & FieldAttributes.Static) == 0) return field.DecodeSignature(provider, default).Text;
        }
        return "int";
    }

    GenericContext Context(TypeDefinition type) => new(
        [.. type.GetGenericParameters().Select(h => reader.GetString(reader.GetGenericParameter(h).Name))],
        []);

    string GenericSuffix(GenericParameterHandleCollection parameters) => parameters.Count == 0
        ? ""
        : $"<{string.Join(", ", parameters.Select(h => reader.GetString(reader.GetGenericParameter(h).Name)))}>";

    string TypeText(EntityHandle handle, GenericContext context) => provider.Decode(handle, context).Text;

    // Members of one type, sorted so the listing is stable: metadata order is declaration
    // order, which would make the diff churn on an edit that changed nothing a caller sees.
    // Enum values are the exception - their order is their meaning, so it is kept.
    IEnumerable<string> Members(TypeDefinition type)
    {
        if (BaseFullName(type) == "System.Enum")
        {
            return type.GetFields()
                .Select(reader.GetFieldDefinition)
                .Where(f => (f.Attributes & FieldAttributes.Static) != 0)
                .Select(f => $"{reader.GetString(f.Name)} = {EnumValue(f)},");
        }

        var context = Context(type);
        var lines = new List<(int Rank, string Text)>();

        foreach (var handle in type.GetFields())
        {
            var field = reader.GetFieldDefinition(handle);
            var access = field.Attributes & FieldAttributes.FieldAccessMask;
            if (access is not (FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem)) continue;
            var name = reader.GetString(field.Name);
            if (name.Contains('<') || HasAttribute(field.GetCustomAttributes(), "CompilerGeneratedAttribute")) continue;

            var modifier = (field.Attributes & FieldAttributes.Literal) != 0 ? "const"
                : (field.Attributes & FieldAttributes.Static) != 0 ? "static"
                : null;
            var readOnly = (field.Attributes & FieldAttributes.InitOnly) != 0 ? "readonly" : null;
            var type_ = Annotate(field.DecodeSignature(provider, context), Nullability(field.GetCustomAttributes(), TypeNullableContext(type)));
            lines.Add((0, Line(field.GetCustomAttributes(), Access(access == FieldAttributes.Public), modifier, readOnly, type_, $"{name};")));
        }

        foreach (var handle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            if (reader.GetString(method.Name) is not ".ctor") continue;
            if (!IsVisibleMethod(method, out var isPublic) || IsGenerated(method)) continue;
            lines.Add((1, Line(method.GetCustomAttributes(), Access(isPublic), null, null, null,
                $"{BareName(type)}({Parameters(method, context)});")));
        }

        foreach (var handle in type.GetProperties())
        {
            var property = reader.GetPropertyDefinition(handle);
            if (HasAttribute(property.GetCustomAttributes(), "CompilerGeneratedAttribute")) continue;
            if (reader.GetString(property.Name).Contains('<')) continue;

            var accessors = property.GetAccessors();
            var getter = accessors.Getter.IsNil ? null : (MethodDefinition?)reader.GetMethodDefinition(accessors.Getter);
            var setter = accessors.Setter.IsNil ? null : (MethodDefinition?)reader.GetMethodDefinition(accessors.Setter);
            var readable = getter is { } g && IsVisibleMethod(g, out _);
            var writable = setter is { } s && IsVisibleMethod(s, out _);
            if (!readable && !writable) continue;

            var anchor = readable ? getter!.Value : setter!.Value;
            IsVisibleMethod(anchor, out var anchorIsPublic);

            var signature = property.DecodeSignature(provider, context);
            var text = new StringBuilder("{ ");
            if (readable) text.Append("get; ");
            if (writable) text.Append(IsInitOnly(setter!.Value, context) ? "init; " : "set; ");
            text.Append('}');

            // An indexer is a property with parameters; its metadata name ("Item") is not
            // what a caller writes.
            var name = signature.ParameterTypes.Length > 0
                ? $"this[{Parameters(readable ? getter!.Value : setter!.Value, context)}]"
                : reader.GetString(property.Name);

            var type_ = Annotate(signature.ReturnType, Nullability(property.GetCustomAttributes(), TypeNullableContext(type)));
            // Same modifiers a method carries, off the accessor: a property turning abstract is a
            // break, and reading only "static" here hid one.
            lines.Add((2, Line(property.GetCustomAttributes(), Access(anchorIsPublic),
                Modifier(anchor.Attributes), null, type_, $"{name} {text}")));
        }

        foreach (var handle in type.GetEvents())
        {
            var declared = reader.GetEventDefinition(handle);
            var adder = declared.GetAccessors().Adder;
            if (adder.IsNil) continue;
            var add = reader.GetMethodDefinition(adder);
            if (!IsVisibleMethod(add, out var isPublic)) continue;
            lines.Add((3, Line(declared.GetCustomAttributes(), Access(isPublic),
                (add.Attributes & MethodAttributes.Static) != 0 ? "static" : null, null,
                TypeText(declared.Type, context), $"{reader.GetString(declared.Name)} {{ add; remove; }}")));
        }

        foreach (var handle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            var name = reader.GetString(method.Name);
            if (name is ".ctor" or ".cctor") continue;
            if (!IsVisibleMethod(method, out var isPublic) || IsGenerated(method)) continue;

            // Accessors are printed with the property or event they belong to. Operators are
            // special-named too, and are surface.
            var isOperator = name.StartsWith("op_", StringComparison.Ordinal);
            if ((method.Attributes & MethodAttributes.SpecialName) != 0 && !isOperator) continue;

            var modifier = Modifier(method.Attributes);

            var methodContext = context with { MethodParameters = [.. method.GetGenericParameters().Select(h => reader.GetString(reader.GetGenericParameter(h).Name))] };
            var signature = method.DecodeSignature(provider, methodContext);
            var returnType = Annotate(signature.ReturnType, Nullability(ReturnParameter(method)?.GetCustomAttributes(), MemberNullableContext(method, type)));

            lines.Add((4, Line(method.GetCustomAttributes(), Access(isPublic), modifier, null, returnType,
                $"{name}{GenericSuffix(method.GetGenericParameters())}({Parameters(method, methodContext)});")));
        }

        return lines.OrderBy(l => l.Rank).ThenBy(l => l.Text, StringComparer.Ordinal).Select(l => l.Text);
    }

    long EnumValue(FieldDefinition field)
    {
        var handle = field.GetDefaultValue();
        if (handle.IsNil) return 0;
        var constant = reader.GetConstant(handle);
        var value = reader.GetBlobReader(constant.Value).ReadConstant(constant.TypeCode);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    string Line(CustomAttributeHandleCollection attributes, string access, string? modifier, string? readOnly, string? returnType, string name)
    {
        var parts = new[] { access, modifier, readOnly, returnType, name }.Where(p => !string.IsNullOrEmpty(p));
        return (HasAttribute(attributes, "ObsoleteAttribute") ? "[Obsolete] " : "") + string.Join(" ", parts);
    }

    static string Access(bool isPublic) => isPublic ? "public" : "protected";

    // Shared by methods and by a property's accessor, so that abstract, virtual and override are
    // recorded wherever they appear: any of them changing is a break a caller feels.
    static string? Modifier(MethodAttributes attributes)
        => (attributes & MethodAttributes.Static) != 0 ? "static"
            : (attributes & MethodAttributes.Abstract) != 0 ? "abstract"
            // Virtual without a new slot overrides the one it inherited.
            : (attributes & MethodAttributes.Virtual) != 0 && (attributes & MethodAttributes.NewSlot) == 0 ? "override"
            : (attributes & MethodAttributes.Virtual) != 0 && (attributes & MethodAttributes.Final) == 0 ? "virtual"
            : null;

    // private protected is not surface. public and protected are.
    static bool IsVisibleMethod(MethodDefinition method, out bool isPublic)
    {
        var access = method.Attributes & MethodAttributes.MemberAccessMask;
        isPublic = access == MethodAttributes.Public;
        return access is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem;
    }

    bool IsGenerated(MethodDefinition method) =>
        reader.GetString(method.Name).Contains('<') || HasAttribute(method.GetCustomAttributes(), "CompilerGeneratedAttribute");

    // An init accessor is a setter whose return carries a required modifier naming IsExternalInit.
    bool IsInitOnly(MethodDefinition setter, GenericContext context) =>
        setter.DecodeSignature(provider, context).ReturnType.Modreq?.EndsWith("IsExternalInit", StringComparison.Ordinal) == true;

    Parameter? ReturnParameter(MethodDefinition method)
    {
        foreach (var handle in method.GetParameters())
        {
            var parameter = reader.GetParameter(handle);
            if (parameter.SequenceNumber == 0) return parameter;
        }
        return null;
    }

    string Parameters(MethodDefinition method, GenericContext context)
    {
        var signature = method.DecodeSignature(provider, context);
        var byPosition = new Dictionary<int, Parameter>();
        foreach (var handle in method.GetParameters())
        {
            var parameter = reader.GetParameter(handle);
            if (parameter.SequenceNumber > 0) byPosition[parameter.SequenceNumber - 1] = parameter;
        }

        var context_ = MemberNullableContext(method, reader.GetTypeDefinition(method.GetDeclaringType()));
        var rendered = new List<string>();
        for (var i = 0; i < signature.ParameterTypes.Length; i++)
        {
            var type = signature.ParameterTypes[i];
            byPosition.TryGetValue(i, out var parameter);
            var attributes = parameter.SequenceNumber > 0 ? parameter.Attributes : default;

            var modifier = type.ByRef
                ? (attributes & ParameterAttributes.Out) != 0 ? "out "
                    : (attributes & ParameterAttributes.In) != 0 ? "in "
                    : "ref "
                : parameter.SequenceNumber > 0 && HasAttribute(parameter.GetCustomAttributes(), "ParamArrayAttribute") ? "params "
                : "";

            var nullability = parameter.SequenceNumber > 0 ? Nullability(parameter.GetCustomAttributes(), context_) : context_;
            var name = parameter.SequenceNumber > 0 ? reader.GetString(parameter.Name) : $"arg{i}";
            var value = parameter.SequenceNumber > 0 && !parameter.GetDefaultValue().IsNil
                ? $" = {DefaultValue(parameter.GetDefaultValue())}"
                : "";

            rendered.Add($"{modifier}{Annotate(type, nullability)} {name}{value}");
        }
        return string.Join(", ", rendered);
    }

    string DefaultValue(ConstantHandle handle)
    {
        var constant = reader.GetConstant(handle);
        var value = reader.GetBlobReader(constant.Value).ReadConstant(constant.TypeCode);
        return value switch
        {
            null => "null",
            string s => $"\"{s}\"",
            bool b => b ? "true" : "false",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "?",
        };
    }

    // Only the outermost annotation is rendered, so only the first byte of the encoding is
    // read: NullableAttribute holds one byte when every part of the type shares a state, and
    // an array in declaration order otherwise. 2 is "may be null"; 1 and 0 are not.
    byte Nullability(CustomAttributeHandleCollection? attributes, byte inherited)
    {
        if (attributes is null) return inherited;
        foreach (var handle in attributes.Value)
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (AttributeName(attribute) != "NullableAttribute") continue;
            var blob = reader.GetBlobBytes(attribute.Value);
            // Prolog(2) value(1) named-argument count(2) for the single-byte form; the array
            // form carries a 4-byte length before its elements.
            if (blob.Length == 5) return blob[2];
            if (blob.Length >= 7) return blob[6];
        }
        return inherited;
    }

    // NullableContextAttribute states what the members around it are, so an annotation is
    // only emitted where a member departs from it. The nearest one wins.
    byte MemberNullableContext(MethodDefinition method, TypeDefinition declaring)
    {
        foreach (var handle in method.GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (AttributeName(attribute) == "NullableContextAttribute")
            {
                var blob = reader.GetBlobBytes(attribute.Value);
                if (blob.Length >= 3) return blob[2];
            }
        }
        return TypeNullableContext(declaring);
    }

    byte TypeNullableContext(TypeDefinition type)
    {
        foreach (var handle in type.GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (AttributeName(attribute) == "NullableContextAttribute")
            {
                var blob = reader.GetBlobBytes(attribute.Value);
                if (blob.Length >= 3) return blob[2];
            }
        }
        return 0;
    }

    // A value type is never annotated, and a context of 2 says nothing about one: the compiler
    // assigns a nullable slot only to reference types and type parameters, and picks whichever
    // context value spares it the most attributes.
    static string Annotate(Sig type, byte nullability) =>
        nullability == 2 && !type.NullableValue && !type.ValueType ? $"{type.Text}?" : type.Text;

    bool HasAttribute(CustomAttributeHandleCollection attributes, string name)
    {
        foreach (var handle in attributes)
        {
            if (AttributeName(reader.GetCustomAttribute(handle)) == name) return true;
        }
        return false;
    }

    string AttributeName(CustomAttribute attribute) => attribute.Constructor.Kind switch
    {
        HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent is { Kind: HandleKind.TypeReference } parent
            ? reader.GetString(reader.GetTypeReference((TypeReferenceHandle)parent).Name)
            : "",
        HandleKind.MethodDefinition => reader.GetString(reader.GetTypeDefinition(
            reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()).Name),
        _ => "",
    };
}

/// <summary>
/// A decoded type: its C# spelling, plus the facts the renderer needs that the text cannot carry.
/// <paramref name="ValueType"/> comes from the signature blob rather than from resolving the type,
/// which is what lets an external struct be told from an external class without reading its assembly.
/// </summary>
internal readonly record struct Sig(string Text, bool ByRef = false, bool NullableValue = false, bool ValueType = false, string? Modreq = null);

/// <summary>The generic parameter names in scope, which the signature blob refers to by index.</summary>
internal readonly record struct GenericContext(ImmutableArray<string> TypeParameters, ImmutableArray<string> MethodParameters);

/// <summary>
/// Renders a signature blob as the C# a caller would write: <c>Span&lt;byte&gt;</c> rather than
/// the <c>Span`1[Byte]</c> that a bare metadata walk produces. Namespaces are dropped, matching
/// how the listing names every other type.
/// </summary>
internal sealed class SignatureRenderer(MetadataReader reader) : ISignatureTypeProvider<Sig, GenericContext>
{
    private static readonly Dictionary<PrimitiveTypeCode, string> Keywords = new()
    {
        [PrimitiveTypeCode.Void] = "void", [PrimitiveTypeCode.Boolean] = "bool", [PrimitiveTypeCode.Byte] = "byte",
        [PrimitiveTypeCode.SByte] = "sbyte", [PrimitiveTypeCode.Char] = "char", [PrimitiveTypeCode.Int16] = "short",
        [PrimitiveTypeCode.UInt16] = "ushort", [PrimitiveTypeCode.Int32] = "int", [PrimitiveTypeCode.UInt32] = "uint",
        [PrimitiveTypeCode.Int64] = "long", [PrimitiveTypeCode.UInt64] = "ulong", [PrimitiveTypeCode.Single] = "float",
        [PrimitiveTypeCode.Double] = "double", [PrimitiveTypeCode.String] = "string", [PrimitiveTypeCode.Object] = "object",
        [PrimitiveTypeCode.IntPtr] = "IntPtr", [PrimitiveTypeCode.UIntPtr] = "UIntPtr",
        [PrimitiveTypeCode.TypedReference] = "TypedReference",
    };

    public Sig Decode(EntityHandle handle, GenericContext context) => handle.Kind switch
    {
        HandleKind.TypeDefinition => GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0),
        HandleKind.TypeReference => GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0),
        HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, context),
        _ => new Sig("?"),
    };

    public Sig GetPrimitiveType(PrimitiveTypeCode typeCode) =>
        new(Keywords.TryGetValue(typeCode, out var keyword) ? keyword : typeCode.ToString(),
            // void is neither, but it is never annotated, and saying so here keeps it that way.
            ValueType: typeCode is not (PrimitiveTypeCode.String or PrimitiveTypeCode.Object));

    public Sig GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var type = r.GetTypeDefinition(handle);
        return new Sig(Name(r.GetString(type.Name), type.IsNested ? GetTypeFromDefinition(r, type.GetDeclaringType(), 0).Text : null),
            ValueType: IsValueType(rawTypeKind));
    }

    public Sig GetTypeFromReference(MetadataReader r, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var type = r.GetTypeReference(handle);
        var name = r.GetString(type.Name);
        // The framework spells decimal as a type reference rather than a primitive code.
        if (name == "Decimal" && r.GetString(type.Namespace) == "System") return new Sig("decimal", ValueType: true);
        var declaring = type.ResolutionScope.Kind == HandleKind.TypeReference
            ? GetTypeFromReference(r, (TypeReferenceHandle)type.ResolutionScope, 0).Text
            : null;
        return new Sig(Name(name, declaring), ValueType: IsValueType(rawTypeKind));
    }

    // A signature spells a type as either ELEMENT_TYPE_VALUETYPE or ELEMENT_TYPE_CLASS before
    // naming it, so the blob already knows what resolving the type would tell us.
    private static bool IsValueType(byte rawTypeKind) => (SignatureTypeKind)rawTypeKind == SignatureTypeKind.ValueType;

    public Sig GetTypeFromSpecification(MetadataReader r, GenericContext context, TypeSpecificationHandle handle, byte rawTypeKind) =>
        r.GetTypeSpecification(handle).DecodeSignature(this, context);

    public Sig GetSZArrayType(Sig elementType) => new($"{elementType.Text}[]");
    public Sig GetArrayType(Sig elementType, ArrayShape shape) => new($"{elementType.Text}[{new string(',', shape.Rank - 1)}]");
    public Sig GetByReferenceType(Sig elementType) => elementType with { ByRef = true };
    public Sig GetPointerType(Sig elementType) => new($"{elementType.Text}*", ValueType: true);
    public Sig GetPinnedType(Sig elementType) => elementType;
    public Sig GetModifiedType(Sig modifier, Sig unmodifiedType, bool isRequired) =>
        isRequired ? unmodifiedType with { Modreq = modifier.Text } : unmodifiedType;

    public Sig GetGenericInstantiation(Sig genericType, ImmutableArray<Sig> typeArguments) =>
        // Nullable<T> is written T?, and is the one "?" that is not an annotation.
        genericType.Text == "Nullable" && typeArguments.Length == 1
            ? new Sig($"{typeArguments[0].Text}?", NullableValue: true, ValueType: true)
            : new Sig($"{genericType.Text}<{string.Join(", ", typeArguments.Select(a => a.Text))}>", ValueType: genericType.ValueType);

    public Sig GetGenericTypeParameter(GenericContext context, int index) =>
        new(index < context.TypeParameters.Length ? context.TypeParameters[index] : $"T{index}");

    public Sig GetGenericMethodParameter(GenericContext context, int index) =>
        new(index < context.MethodParameters.Length ? context.MethodParameters[index] : $"TMethod{index}");

    public Sig GetFunctionPointerType(MethodSignature<Sig> signature) =>
        new($"delegate*<{string.Join(", ", signature.ParameterTypes.Select(p => p.Text).Append(signature.ReturnType.Text))}>");

    private static string Name(string name, string? declaring)
    {
        var tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        return declaring is null ? name : $"{declaring}.{name}";
    }
}
