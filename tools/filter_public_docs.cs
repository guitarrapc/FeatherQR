#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

// Trims XML documentation down to the surface a consumer can reach, and verifies the result.
//
//   dotnet run tools/filter_public_docs.cs -- <documentation.xml>...            trim + verify
//   dotnet run tools/filter_public_docs.cs -- --check <documentation.xml>...    verify only
//
// The assembly is the .dll beside each file. Pass several with a shell glob. Visibility comes
// from its metadata tables, so this needs no package reference and never loads the assembly.

var check = args.Length > 0 && args[0] == "--check";
var documentationPaths = args.Skip(check ? 1 : 0).ToArray();

if (documentationPaths.Length == 0)
{
    Console.Error.WriteLine("usage: filter_public_docs [--check] <documentation.xml>...");
    return 2;
}

var failed = false;
foreach (var path in documentationPaths)
    failed |= !Process(path, check);

return failed ? 1 : 0;

bool Process(string documentationPath, bool verifyOnly)
{
    var assemblyPath = Path.ChangeExtension(documentationPath, ".dll");
    if (!File.Exists(assemblyPath))
    {
        Console.Error.WriteLine($"No assembly beside {documentationPath}: expected {Path.GetFileName(assemblyPath)}.");
        return false;
    }

    XDocument document;
    try
    {
        document = XDocument.Load(documentationPath);
    }
    catch (System.Xml.XmlException e)
    {
        // Deleted so the next build regenerates it. The compiler would otherwise stay up to date.
        if (!verifyOnly) File.Delete(documentationPath);
        Console.Error.WriteLine($"{documentationPath} was not valid XML ({e.Message}). Run the build again to regenerate it.");
        return false;
    }

    using var stream = File.OpenRead(assemblyPath);
    using var peReader = new PEReader(stream);
    var reader = peReader.GetMetadataReader();

    // Keys are "T:Full.Type.Name" and "Full.Type.Name.MemberName", not full documentation IDs,
    // which would mean re-encoding every parameter type. Overloads therefore share a key: this
    // may keep too much, never too little.
    var visible = new HashSet<string>(StringComparer.Ordinal);
    var visibleTypes = new List<string>();

    foreach (var handle in reader.TypeDefinitions)
    {
        var type = reader.GetTypeDefinition(handle);
        if (!IsVisibleType(type)) continue;

        var typeName = FullName(type);
        visible.Add($"T:{typeName}");
        visibleTypes.Add($"T:{typeName}");

        foreach (var methodHandle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(methodHandle);
            if (!IsVisible(method.Attributes)) continue;
            var name = reader.GetString(method.Name);
            visible.Add($"{typeName}.{(name is ".ctor" or ".cctor" ? "#ctor" : name)}");
        }

        foreach (var fieldHandle in type.GetFields())
        {
            var field = reader.GetFieldDefinition(fieldHandle);
            if ((field.Attributes & FieldAttributes.FieldAccessMask) is FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem)
                visible.Add($"{typeName}.{reader.GetString(field.Name)}");
        }

        // Properties and events have no accessibility of their own. One visible accessor is
        // enough to make the member reachable.
        foreach (var propertyHandle in type.GetProperties())
        {
            var property = reader.GetPropertyDefinition(propertyHandle);
            var accessors = property.GetAccessors();
            if (IsVisibleAccessor(accessors.Getter) || IsVisibleAccessor(accessors.Setter))
                visible.Add($"{typeName}.{reader.GetString(property.Name)}");
        }

        foreach (var eventHandle in type.GetEvents())
        {
            var declared = reader.GetEventDefinition(eventHandle);
            var accessors = declared.GetAccessors();
            if (IsVisibleAccessor(accessors.Adder) || IsVisibleAccessor(accessors.Remover))
                visible.Add($"{typeName}.{reader.GetString(declared.Name)}");
        }
    }

    // An empty surface means the read went wrong, not that the library has no API.
    if (visible.Count == 0)
    {
        Console.Error.WriteLine($"Read no reachable surface out of {Path.GetFileName(assemblyPath)}; refusing to touch {Path.GetFileName(documentationPath)}.");
        return false;
    }

    var members = document.Descendants("member").ToList();
    var unreachable = members.Where(member => !visible.Contains(KeyOf((string?)member.Attribute("name")))).ToList();

    // Rewrite only when something changes, so the file and its timestamp are left alone.
    if (!verifyOnly && unreachable.Count > 0)
    {
        foreach (var member in unreachable) member.Remove();
        document.Save(documentationPath);
        members = document.Descendants("member").ToList();
        unreachable.Clear();
    }

    // Both directions: entries no caller can reach, and public types that lost their docs.
    var documented = members.Select(member => (string?)member.Attribute("name")).ToHashSet(StringComparer.Ordinal);
    var undocumented = visibleTypes.Where(id => !documented.Contains(id)).ToArray();

    if (unreachable.Count > 0)
    {
        Console.Error.WriteLine($"{documentationPath}: {unreachable.Count} member(s) no caller can reach. Run without --check to remove them.");
        foreach (var member in unreachable.Take(5)) Console.Error.WriteLine($"  {(string?)member.Attribute("name")}");
    }

    if (undocumented.Length > 0)
    {
        Console.Error.WriteLine($"{documentationPath}: {undocumented.Length} publicly reachable type(s) have no documentation.");
        foreach (var id in undocumented.Take(5)) Console.Error.WriteLine($"  {id}");
    }

    if (unreachable.Count > 0 || undocumented.Length > 0) return false;

    Console.WriteLine($"{documentationPath}: {members.Count} member(s), all reachable.");
    return true;

    // "M:Ns.Type`1.Method``1(System.Int32@)~T" -> "Ns.Type`1.Method". A type's single backtick
    // arity is part of its metadata name, so only the method's double one is cut.
    static string KeyOf(string? id)
    {
        if (id is null || id.Length < 3 || id[1] != ':') return "";
        var body = id[2..];
        var paren = body.IndexOf('(');
        if (paren >= 0) body = body[..paren];
        if (id[0] == 'T') return $"T:{body}";
        var arity = body.LastIndexOf("``", StringComparison.Ordinal);
        return arity >= 0 ? body[..arity] : body;
    }

    // Documentation IDs spell nesting with a dot, the same as a namespace.
    string FullName(TypeDefinition type)
    {
        var name = reader.GetString(type.Name);
        if (type.IsNested) return $"{FullName(reader.GetTypeDefinition(type.GetDeclaringType()))}.{name}";
        var space = reader.GetString(type.Namespace);
        return space.Length == 0 ? name : $"{space}.{name}";
    }

    // Nested protected counts as reachable: a consumer gets at those by deriving. Compiler-generated
    // nested types do not: a C# 14 extension block emits public marker types with unspeakable names
    // (<G>$hash, <M>$hash) that no caller can spell and no documentation can name.
    bool IsVisibleType(TypeDefinition type)
    {
        var visibility = type.Attributes & TypeAttributes.VisibilityMask;
        if (!type.IsNested) return visibility == TypeAttributes.Public;
        if (reader.GetString(type.Name).Contains('<')) return false;
        return visibility is TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem
            && IsVisibleType(reader.GetTypeDefinition(type.GetDeclaringType()));
    }

    // Internal and private-protected are not reachable: the only friend is the test project.
    static bool IsVisible(MethodAttributes attributes)
        => (attributes & MethodAttributes.MemberAccessMask) is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem;

    bool IsVisibleAccessor(MethodDefinitionHandle handle)
        => !handle.IsNil && IsVisible(reader.GetMethodDefinition(handle).Attributes);
}
