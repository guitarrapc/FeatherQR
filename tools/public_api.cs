#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../src/FeatherQR.SkiaSharp/FeatherQR.SkiaSharp.csproj
#:property EnableAotAnalyzer=false
#:property EnableTrimAnalyzer=false
#:property EnableSingleFileAnalyzer=false

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

// Prints the exported surface of FeatherQR and FeatherQR.SkiaSharp as one sorted, diffable listing.
//
//   dotnet run tools/public_api.cs                    plain text, to stdout
//   dotnet run tools/public_api.cs -- -o api.txt      plain text, to a file
//   dotnet run tools/public_api.cs -- --html -o p     a filterable page, to a file
//
// Write the page into the Playground's wwwroot and one output serves both ways of looking
// at it: it is there when the Playground runs locally, and publish copies it to the Pages
// site. That path is generated, so it is gitignored rather than committed:
//
//   dotnet run tools/public_api.cs -- --html -o src/FeatherQR.Playground/wwwroot/api/index.html
//
// This is a viewer, not a gate. Nothing in CI validates it and nothing fails when the
// surface changes: breaking changes are already caught by package validation against the
// released baseline, which is the only surface check worth failing a build over.
//
// One target framework is enough. No exported type has a framework-conditional member -
// the conditional compilation in the library all sits on internal types - so net10.0
// represents netstandard2.0, netstandard2.1 and net8.0 as well.

var outputPath = GetOutputPath(args);
var wantsHtml = args.Contains("--html");
// Both shipped assemblies, in the order the packages depend on each other. The project
// reference to the rendering package brings the core along, so one reference covers both.
var assemblies = new[] { typeof(FeatherQR.QRCodeData).Assembly, typeof(FeatherQR.SkiaSharp.SymbolRenderer).Assembly };
var version = assemblies[0].GetName().Version;
var nullability = new NullabilityInfoContext();

// Doc text, keyed by the documentation ID the compiler emits. Exported types never collide
// across the two assemblies; the compiler-generated internals both carry (PolySharp's
// polyfills, Microsoft.CodeAnalysis.EmbeddedAttribute) do, and either copy will do.
var docs = new Dictionary<string, (string Summary, string Remarks)>(StringComparer.Ordinal);
foreach (var entry in assemblies.SelectMany(LoadDocs)) docs.TryAdd(entry.Key, entry.Value);

// Source links are opt-in because they are only trustworthy at release. SourceLink pins the
// commit that was built, so a page generated from an unpushed local commit would link to a
// SHA GitHub has never seen. The release workflow builds at the tag and passes the flag.
// One PDB per assembly: a method's token only means something in its own PDB.
var pdbs = args.Contains("--source-links")
    ? assemblies.Select(a => (Assembly: a, Pdb: LoadPdb(a))).Where(p => p.Pdb is not null).ToDictionary(p => p.Assembly, p => p.Pdb!.Value)
    : [];
var sourceCommit = pdbs.Count == 0 ? null : ShortCommit(pdbs.Values.First());

var keywords = new Dictionary<Type, string>
{
    [typeof(void)] = "void", [typeof(bool)] = "bool", [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte",
    [typeof(char)] = "char", [typeof(short)] = "short", [typeof(ushort)] = "ushort", [typeof(int)] = "int",
    [typeof(uint)] = "uint", [typeof(long)] = "long", [typeof(ulong)] = "ulong", [typeof(float)] = "float",
    [typeof(double)] = "double", [typeof(decimal)] = "decimal", [typeof(string)] = "string", [typeof(object)] = "object",
};

// Compiler-generated nested types (the unspeakable <G>$hash markers a C# 14 extension block emits)
// are skipped here as check_public_api.cs and filter_public_docs.cs skip them.
var types = assemblies.SelectMany(a => a.GetExportedTypes())
    .Where(t => !t.Name.Contains('<'))
    .OrderBy(t => t.Namespace, StringComparer.Ordinal)
    .ThenBy(FullName, StringComparer.Ordinal)
    .ToArray();

// Both renderers read the same model, so the page and the text listing can never disagree
// about what the surface is.
var model = types
    .Select(t => MemberEntries(t) is var members
        ? (Type: t, Name: FullName(t), Header: TypeHeader(t), Obsolete: IsObsolete(t), DocId: DocIdOfType(t),
           // A type has no sequence points of its own, so it borrows the file its first member
           // is in and drops the line: the right file, without claiming to know the exact line
           // the declaration starts on.
           Href: members.Select(m => m.Href).FirstOrDefault(h => h is not null)?.Split('#')[0],
           Members: members)
        : default)
    .ToArray();

var rendered = wantsHtml ? RenderHtml() : RenderText();

if (outputPath is null)
{
    Console.Out.Write(rendered);
}
else
{
    var full = Path.GetFullPath(outputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllText(full, rendered);
    Console.Error.WriteLine($"{model.Length} exported types written to {full}");
}
return 0;

(int Rank, string Kind, string Name, string Text, string DocId, string? Href)[] MemberEntries(Type type) => type.IsEnum
    ? [.. type.GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (0, "value", f.Name, $"{f.Name} = {Convert.ToInt64(f.GetRawConstantValue())},", DocIdOfField(f), (string?)null))]
    : [.. Members(type)];

// Documentation IDs, as the compiler spells them in the XML file: nested types join with a
// dot rather than reflection's plus, byref is a trailing @, a constructed generic uses braces,
// and a conversion operator carries its return type after a tilde because the parameter list
// alone does not tell two of them apart.
string DocIdOfType(Type type) => $"T:{DocTypeName(type)}";
string DocIdOfField(FieldInfo field) => $"F:{DocTypeName(field.DeclaringType!)}.{field.Name}";
string DocIdOfEvent(EventInfo evt) => $"E:{DocTypeName(evt.DeclaringType!)}.{evt.Name}";

string DocIdOfProperty(PropertyInfo property)
{
    var indexers = property.GetIndexParameters();
    var arguments = indexers.Length == 0 ? "" : $"({string.Join(",", indexers.Select(p => DocParam(p.ParameterType)))})";
    return $"P:{DocTypeName(property.DeclaringType!)}.{property.Name}{arguments}";
}

string DocIdOfMethod(MethodBase method)
{
    var name = method is ConstructorInfo ? "#ctor" : method.Name;
    var arity = method.IsGenericMethodDefinition ? $"``{method.GetGenericArguments().Length}" : "";
    var parameters = method.GetParameters();
    var arguments = parameters.Length == 0 ? "" : $"({string.Join(",", parameters.Select(p => DocParam(p.ParameterType)))})";
    var conversion = method is MethodInfo { Name: "op_Implicit" or "op_Explicit" } m ? $"~{DocParam(m.ReturnType)}" : "";
    return $"M:{DocTypeName(method.DeclaringType!)}.{name}{arity}{arguments}{conversion}";
}

string DocTypeName(Type type) => (type.FullName ?? type.Name).Replace('+', '.');

string DocParam(Type type)
{
    if (type.IsByRef) return $"{DocParam(type.GetElementType()!)}@";
    if (type.IsPointer) return $"{DocParam(type.GetElementType()!)}*";
    if (type.IsArray) return $"{DocParam(type.GetElementType()!)}[{new string(',', type.GetArrayRank() - 1)}]";
    if (type.IsGenericParameter) return type.DeclaringMethod is null ? $"`{type.GenericParameterPosition}" : $"``{type.GenericParameterPosition}";

    if (type.IsConstructedGenericType)
    {
        var bare = DocTypeName(type.GetGenericTypeDefinition());
        var tick = bare.IndexOf('`');
        if (tick >= 0) bare = bare[..tick];
        return $"{bare}{{{string.Join(",", type.GetGenericArguments().Select(DocParam))}}}";
    }

    return DocTypeName(type);
}

// The PDB, plus the SourceLink document map read out of it. SourceLink is on by default in the
// .NET SDK, so this needs no package: the map is one entry, a local path prefix to a
// raw.githubusercontent URL carrying the commit that was built.
(MetadataReader Reader, (string Prefix, string Template)[] Map)? LoadPdb(Assembly assembly)
{
    var path = Path.ChangeExtension(assembly.Location, ".pdb");
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"No PDB beside {Path.GetFileName(assembly.Location)}; source links are omitted.");
        return null;
    }

    // The provider owns the memory the reader points into, so it deliberately outlives this
    // method rather than being disposed here.
    var reader = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(path)).GetMetadataReader();
    var kind = new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A");

    foreach (var handle in reader.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
    {
        var info = reader.GetCustomDebugInformation(handle);
        if (reader.GetGuid(info.Kind) != kind) continue;

        using var json = JsonDocument.Parse(reader.GetBlobBytes(info.Value));
        var map = json.RootElement.GetProperty("documents").EnumerateObject()
            .Select(e => (Prefix: e.Name, Template: e.Value.GetString() ?? ""))
            .ToArray();
        if (map.Length > 0) return (reader, map);
    }

    Console.Error.WriteLine("The PDB carries no SourceLink map; source links are omitted.");
    return null;
}

string? ShortCommit((MetadataReader Reader, (string Prefix, string Template)[] Map) pdb)
{
    var template = pdb.Map[0].Template;
    var parts = template.Replace("https://raw.githubusercontent.com/", "").Split('/');
    return parts.Length >= 3 && parts[2].Length >= 7 ? parts[2][..7] : null;
}

// Where a member is declared, as a GitHub blob URL for the built commit. Only members with IL
// have sequence points, so fields and enum values have no location and get no link.
string? SourceHref(MethodBase? method)
{
    if (method is null || !pdbs.TryGetValue(method.Module.Assembly, out var pdb)) return null;

    try
    {
        var (reader, map) = pdb;
        var debug = reader.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(method.MetadataToken & 0xFFFFFF));
        if (debug.Document.IsNil) return null;

        var file = reader.GetString(reader.GetDocument(debug.Document).Name);
        var line = debug.GetSequencePoints().Where(p => !p.IsHidden).Select(p => p.StartLine).FirstOrDefault();
        var blob = BlobUrl(file, map);
        return blob is null ? null : line > 0 ? $"{blob}#L{line}" : blob;
    }
    catch
    {
        // A token with no row in the PDB's debug table: no location, not a failure.
        return null;
    }
}

// raw.githubusercontent serves the file; github.com/blob is the page a reader wants, and the
// only one of the two that honours a #L line anchor.
string? BlobUrl(string documentPath, (string Prefix, string Template)[] map)
{
    foreach (var (prefix, template) in map)
    {
        var head = prefix.TrimEnd('*');
        if (!documentPath.StartsWith(head, StringComparison.OrdinalIgnoreCase)) continue;

        var raw = template.Replace("*", documentPath[head.Length..].Replace('\\', '/'));
        const string RawHost = "https://raw.githubusercontent.com/";
        if (!raw.StartsWith(RawHost, StringComparison.Ordinal)) return raw;

        var parts = raw[RawHost.Length..].Split('/', 4);
        return parts.Length < 4 ? raw : $"https://github.com/{parts[0]}/{parts[1]}/blob/{parts[2]}/{parts[3]}";
    }
    return null;
}

Dictionary<string, (string Summary, string Remarks)> LoadDocs(Assembly assembly)
{
    // The library generates its documentation file and ships it in the package, and a project
    // reference copies it next to the assembly, so there is nothing to build here.
    var path = Path.ChangeExtension(assembly.Location, ".xml");
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"No XML documentation beside {Path.GetFileName(assembly.Location)}; signatures will carry no doc text.");
        return [];
    }

    var map = new Dictionary<string, (string Summary, string Remarks)>(StringComparer.Ordinal);
    foreach (var member in System.Xml.Linq.XDocument.Load(path).Descendants("member"))
    {
        var id = member.Attribute("name")?.Value;
        if (id is null) continue;

        var summary = member.Element("summary") is { } s ? FlattenDoc(s) : "";
        var remarks = member.Element("remarks") is { } r ? FlattenDoc(r) : "";
        if (summary.Length > 0 || remarks.Length > 0) map[id] = (summary, remarks);
    }
    return map;
}

// Doc XML is mixed content. Keep the prose, and render the references a reader cares about
// as the short name they would type, dropping the ID prefix the compiler adds.
string FlattenDoc(System.Xml.Linq.XElement element)
{
    var sb = new StringBuilder();
    Walk(element);
    return string.Join(" ", sb.ToString().Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    void Walk(System.Xml.Linq.XElement node)
    {
        foreach (var child in node.Nodes())
        {
            switch (child)
            {
                case System.Xml.Linq.XText text:
                    sb.Append(text.Value);
                    break;
                case System.Xml.Linq.XElement e when e.Name == "see" || e.Name == "seealso":
                    var reference = e.Attribute("cref")?.Value ?? e.Attribute("langword")?.Value ?? "";
                    var colon = reference.IndexOf(':');
                    if (colon == 1) reference = reference[(colon + 1)..];
                    // Drop the parameter list before taking the last segment. Without this a
                    // method cref ends at the last dot inside its own arguments, which reads
                    // as a stray parameter type rather than the method being pointed at.
                    var paren = reference.IndexOf('(');
                    if (paren >= 0) reference = reference[..paren];
                    // Take the member name before stripping the arity tick, not after: on a generic type
                    // the first tick sits in the type name, so cutting there threw the member away and
                    // every member of SymbolImageBuilderBase`1 rendered as the type itself.
                    var name = reference[(reference.LastIndexOf('.') + 1)..];
                    var tick = name.IndexOf('`');
                    if (tick >= 0) name = name[..tick];
                    sb.Append(' ').Append(name).Append(' ');
                    break;
                case System.Xml.Linq.XElement e when e.Name == "paramref" || e.Name == "typeparamref":
                    sb.Append(' ').Append(e.Attribute("name")?.Value ?? "").Append(' ');
                    break;
                case System.Xml.Linq.XElement e:
                    Walk(e);
                    break;
            }
        }
    }
}

string RenderText()
{
    var sb = new StringBuilder();
    foreach (var assembly in assemblies) sb.AppendLine($"// {assembly.GetName().Name} {assembly.GetName().Version}");
    sb.AppendLine($"// {model.Length} exported types");

    foreach (var group in model.GroupBy(m => m.Type.Namespace).OrderBy(g => g.Key, StringComparer.Ordinal))
    {
        sb.AppendLine();
        sb.AppendLine($"namespace {group.Key}");
        sb.AppendLine("{");
        foreach (var type in group)
        {
            sb.AppendLine();
            if (type.Obsolete) sb.AppendLine("    [Obsolete]");
            sb.AppendLine($"    {type.Header}");
            sb.AppendLine("    {");
            foreach (var member in type.Members) sb.AppendLine($"        {member.Text}");
            sb.AppendLine("    }");
        }
        sb.AppendLine("}");
    }

    return sb.ToString();
}

string RenderHtml()
{
    var outline = new StringBuilder();
    var body = new StringBuilder();

    foreach (var group in model.GroupBy(m => m.Type.Namespace).OrderBy(g => g.Key, StringComparer.Ordinal))
    {
        outline.AppendLine($"""        <p class="toc-ns">{Escape(group.Key!)}</p>""");
        body.AppendLine($"""      <section class="ns">""");
        body.AppendLine($"""        <h2>namespace {Escape(group.Key!)}</h2>""");

        foreach (var type in group)
        {
            var id = Escape(type.Name);
            outline.AppendLine($"""        <a class="toc-item" href="#{id}" data-for="{id}">{Escape(BareName(type.Type))}</a>""");

            // data-name matches the type alone, data-text matches its members and their doc
            // text too. The filter needs both: a hit on the type name keeps every member, a
            // hit inside one member keeps only that member.
            var haystack = $"{type.Name} {type.Header} {Summary(type.DocId)} {string.Join(" ", type.Members.Select(m => m.Kind + " " + m.Text + " " + Summary(m.DocId)))}";
            body.AppendLine($"""        <article class="type" id="{id}" data-name="{Escape(type.Name.ToLowerInvariant())}" data-text="{Escape(haystack.ToLowerInvariant())}">""");

            // Kind first, then the name, then the declaration. Reading "method" or "property"
            // off a heading beats inferring it from whether the signature ends in parentheses
            // or in an accessor list.
            body.AppendLine($"""          <h3><a class="anchor" href="#{id}" aria-label="Link to {id}">#</a><span class="kind">{TypeKind(type.Type)}</span> {Named(BareName(type.Type), type.Href, "source file")}{Tag(type.Obsolete)}</h3>""");
            body.AppendLine($"""          <pre class="sig"><code>{Escape(type.Header)}</code></pre>""");
            body.Append(Doc(type.DocId, "          "));

            if (type.Members.Length > 0)
            {
                body.AppendLine("""          <div class="members">""");
                foreach (var member in type.Members)
                {
                    var obsolete = member.Text.StartsWith("[Obsolete] ", StringComparison.Ordinal);
                    var text = obsolete ? member.Text["[Obsolete] ".Length..] : member.Text;
                    var searchable = $"{member.Kind} {text} {Summary(member.DocId)}".ToLowerInvariant();
                    body.AppendLine($"""            <div class="member" data-text="{Escape(searchable)}">""");

                    // An enum value is its own heading - "value Deflate" above "Deflate = 1,"
                    // would say the same thing twice - so it keeps the compact form.
                    if (member.Kind != "value")
                    {
                        body.AppendLine($"""              <h4><span class="kind">{member.Kind}</span> {Named(member.Name, member.Href, "source")}{Tag(obsolete)}</h4>""");
                        body.AppendLine($"""              <pre class="sig"><code>{Escape(text)}</code></pre>""");
                    }
                    else
                    {
                        body.AppendLine($"""              <pre class="sig"><code>{Escape(text)}</code></pre>{Tag(obsolete)}""");
                    }

                    body.Append(Doc(member.DocId, "              "));
                    body.AppendLine("            </div>");
                }
                body.AppendLine("          </div>");
            }

            body.AppendLine("        </article>");
        }

        body.AppendLine("      </section>");
    }

    return Shell(outline.ToString().TrimEnd(), body.ToString().TrimEnd());

    static string Tag(bool obsolete) => obsolete ? """ <span class="tag">obsolete</span>""" : "";

    // The name is a link to the declaration when a location is known, and plain text when it
    // is not, so a reader never clicks a name that goes nowhere.
    static string Named(string name, string? href, string what) => href is null
        ? $"""<span class="name">{Escape(name)}</span>"""
        : $"""<a class="name" href="{Escape(href)}" title="View {what} on GitHub" target="_blank" rel="noopener noreferrer">{Escape(name)}</a>""";

    // Summaries are what a reader scans, so they are always shown. Remarks are the reasoning
    // behind them and often run several times longer, which buries the signatures when left
    // open, so they fold away behind a toggle. The filter opens the ones it matched inside.
    string Doc(string docId, string indent)
    {
        if (!docs.TryGetValue(docId, out var text)) return "";

        var sb = new StringBuilder();
        if (text.Summary.Length > 0) sb.AppendLine($"""{indent}<p class="doc">{Escape(text.Summary)}</p>""");
        if (text.Remarks.Length > 0)
        {
            sb.AppendLine($"""{indent}<details class="notes" data-text="{Escape(text.Remarks.ToLowerInvariant())}">""");
            sb.AppendLine($"""{indent}  <summary>Notes</summary>""");
            sb.AppendLine($"""{indent}  <p class="doc remarks">{Escape(text.Remarks)}</p>""");
            sb.AppendLine($"""{indent}</details>""");
        }
        return sb.ToString();
    }

    // Everything on the page is searchable, remarks included: the filter must not know less
    // than the reader can see.
    string Summary(string docId) => docs.TryGetValue(docId, out var text) ? $"{text.Summary} {text.Remarks}" : "";
}

// Members of one type, sorted so the listing is stable across builds. Reflection makes no
// promise about declaration order, so an unsorted dump would churn on every rebuild and
// the diff would stop meaning anything.
IEnumerable<(int Rank, string Kind, string Name, string Text, string DocId, string? Href)> Members(Type type)
{
    const BindingFlags Scope = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    var lines = new List<(int Rank, string Kind, string Name, string Text, string DocId, string? Href)>();

    foreach (var field in type.GetFields(Scope))
    {
        if (!Visible(field.IsPublic, field.IsFamily, field.IsFamilyOrAssembly) || Generated(field)) continue;
        var modifier = field.IsLiteral ? "const" : field.IsStatic ? "static" : null;
        var readOnly = field.IsInitOnly ? "readonly" : null;
        lines.Add((0, field.IsLiteral ? "constant" : "field", field.Name, Line(field, Access(field.IsPublic), modifier, readOnly, TypeName(field.FieldType, FieldNullability(field)), field.Name) + ";", DocIdOfField(field), null));
    }

    foreach (var ctor in type.GetConstructors(Scope))
    {
        if (!Visible(ctor.IsPublic, ctor.IsFamily, ctor.IsFamilyOrAssembly) || Generated(ctor)) continue;
        lines.Add((1, "constructor", BareName(type), Line(ctor, Access(ctor.IsPublic), null, null, null, $"{BareName(type)}({Parameters(ctor)})") + ";", DocIdOfMethod(ctor), SourceHref(ctor)));
    }

    foreach (var property in type.GetProperties(Scope))
    {
        if (Generated(property)) continue;
        var getter = property.GetMethod;
        var setter = property.SetMethod;
        var readable = getter is not null && Visible(getter.IsPublic, getter.IsFamily, getter.IsFamilyOrAssembly);
        var writable = setter is not null && Visible(setter.IsPublic, setter.IsFamily, setter.IsFamilyOrAssembly);
        if (!readable && !writable) continue;

        var accessors = new StringBuilder("{ ");
        if (readable) accessors.Append("get; ");
        if (writable) accessors.Append(IsInitOnly(setter!) ? "init; " : "set; ");
        accessors.Append('}');

        var anchor = readable ? getter! : setter!;
        var name = property.GetIndexParameters().Length > 0 ? $"this[{Parameters(anchor)}]" : property.Name;
        lines.Add((2, property.GetIndexParameters().Length > 0 ? "indexer" : "property", name, Line(property, Access(anchor.IsPublic), anchor.IsStatic ? "static" : null, null,
            TypeName(property.PropertyType, PropertyNullability(property)), $"{name} {accessors}"), DocIdOfProperty(property), SourceHref(anchor)));
    }

    foreach (var evt in type.GetEvents(Scope))
    {
        if (evt.AddMethod is not { } add || !Visible(add.IsPublic, add.IsFamily, add.IsFamilyOrAssembly)) continue;
        lines.Add((3, "event", evt.Name, Line(evt, Access(add.IsPublic), add.IsStatic ? "static" : null, null,
            TypeName(evt.EventHandlerType!, null), $"{evt.Name} {{ add; remove; }}"), DocIdOfEvent(evt), SourceHref(add)));
    }

    foreach (var method in type.GetMethods(Scope))
    {
        if (!Visible(method.IsPublic, method.IsFamily, method.IsFamilyOrAssembly) || Generated(method) || IsAccessor(method)) continue;
        var modifier = method.IsStatic ? "static"
            : method.IsAbstract ? "abstract"
            : method.GetBaseDefinition() != method ? "override"
            : method.IsVirtual && !method.IsFinal ? "virtual"
            : null;
        var name = $"{method.Name}{GenericSuffix(method.GetGenericArguments())}({Parameters(method)})";
        lines.Add((4, method.Name.StartsWith("op_", StringComparison.Ordinal) ? "operator" : "method", method.Name, Line(method, Access(method.IsPublic), modifier, null,
            TypeName(method.ReturnType, ParameterNullability(method.ReturnParameter)), name) + ";", DocIdOfMethod(method), SourceHref(method)));
    }

    return lines.OrderBy(l => l.Rank).ThenBy(l => l.Text, StringComparer.Ordinal);
}

string Line(MemberInfo member, string access, string? modifier, string? readOnly, string? returnType, string name)
{
    var parts = new[] { access, modifier, readOnly, returnType, name }.Where(p => !string.IsNullOrEmpty(p));
    return (IsObsolete(member) ? "[Obsolete] " : "") + string.Join(" ", parts);
}

string TypeKind(Type type) => type.IsEnum ? "enum"
    : type.IsInterface ? "interface"
    : typeof(Delegate).IsAssignableFrom(type) ? "delegate"
    : type.IsValueType ? "struct"
    : "class";

string TypeHeader(Type type)
{
    var kind = TypeKind(type);

    var modifiers = new List<string> { type.IsPublic || type.IsNestedPublic ? "public" : "protected" };
    if (type is { IsAbstract: true, IsSealed: true, IsEnum: false, IsInterface: false }) modifiers.Add("static");
    else if (type.IsAbstract && !type.IsInterface) modifiers.Add("abstract");
    else if (type.IsSealed && !type.IsEnum && !type.IsValueType) modifiers.Add("sealed");
    if (type.IsValueType && !type.IsEnum && TypeHasAttribute(type, "IsReadOnlyAttribute")) modifiers.Add("readonly");
    modifiers.Add(kind);

    var header = $"{string.Join(" ", modifiers)} {BareName(type)}{GenericSuffix(type.GetGenericArguments())}";

    var bases = new List<string>();
    if (type is { IsClass: true, BaseType: not null } && type.BaseType != typeof(object) && !typeof(Delegate).IsAssignableFrom(type))
    {
        bases.Add(TypeName(type.BaseType, null));
    }

    // Interfaces the base type already carries are noise. Only what this type adds is surface.
    var inherited = type.BaseType?.GetInterfaces() ?? [];
    bases.AddRange(type.GetInterfaces().Except(inherited).Select(i => TypeName(i, null)).OrderBy(n => n, StringComparer.Ordinal));

    if (type.IsEnum) bases.Add(TypeName(Enum.GetUnderlyingType(type), null));

    return bases.Count > 0 ? $"{header} : {string.Join(", ", bases)}" : header;
}

string Parameters(MethodBase method) => string.Join(", ", method.GetParameters().Select(p =>
{
    var modifier = p.ParameterType.IsByRef
        ? p.IsOut ? "out " : p.IsIn ? "in " : "ref "
        : ParameterHasAttribute(p, "ParamArrayAttribute") ? "params " : "";
    var value = p.HasDefaultValue ? $" = {Literal(p.RawDefaultValue)}" : "";
    return $"{modifier}{TypeName(p.ParameterType, ParameterNullability(p))} {p.Name}{value}";
}));

// Friendly C# spelling. Reflection renders a generic as Span'1[Byte], which is unreadable
// in a listing meant to be skimmed and compared by eye.
string TypeName(Type type, NullabilityInfo? info)
{
    var suffix = info is { ReadState: NullabilityState.Nullable } && Nullable.GetUnderlyingType(Bare(type)) is null ? "?" : "";
    return Core(type) + suffix;

    static Type Bare(Type t) => t.IsByRef ? t.GetElementType()! : t;

    string Core(Type t)
    {
        if (t.IsByRef) return Core(t.GetElementType()!);
        if (t.IsArray) return $"{Core(t.GetElementType()!)}[{new string(',', t.GetArrayRank() - 1)}]";
        if (t.IsPointer) return $"{Core(t.GetElementType()!)}*";

        if (Nullable.GetUnderlyingType(t) is { } underlying) return $"{Core(underlying)}?";
        if (keywords.TryGetValue(t, out var keyword)) return keyword;
        if (t.IsGenericType) return $"{BareName(t)}<{string.Join(", ", t.GetGenericArguments().Select(Core))}>";
        return BareName(t);
    }
}

NullabilityInfo? ParameterNullability(ParameterInfo p) { try { return nullability.Create(p); } catch { return null; } }
NullabilityInfo? PropertyNullability(PropertyInfo p) { try { return nullability.Create(p); } catch { return null; } }
NullabilityInfo? FieldNullability(FieldInfo f) { try { return nullability.Create(f); } catch { return null; } }

string GenericSuffix(Type[] arguments) => arguments.Length == 0 ? "" : $"<{string.Join(", ", arguments.Select(a => a.Name))}>";

// Nested types read as Outer.Inner rather than reflection's Outer+Inner.
string BareName(Type type)
{
    // A generic parameter reports the type that declared it as its declaring type, which would
    // otherwise qualify it as if it were nested: TSelf, not SymbolImageBuilderBase.TSelf.
    if (type.IsGenericParameter) return type.Name;

    var name = type.Name;
    var tick = name.IndexOf('`');
    if (tick >= 0) name = name[..tick];
    return type.IsNested ? $"{BareName(type.DeclaringType!)}.{name}" : name;
}

string FullName(Type type) => $"{type.Namespace}.{BareName(type)}";

// private protected is not surface. public and protected are.
bool Visible(bool isPublic, bool isFamily, bool isFamilyOrAssembly) => isPublic || isFamily || isFamilyOrAssembly;

string Access(bool isPublic) => isPublic ? "public" : "protected";

bool Generated(MemberInfo member) =>
    member.Name.Contains('<') || member.GetCustomAttributes(typeof(CompilerGeneratedAttribute), false).Length > 0;

bool IsAccessor(MethodInfo method) => method.IsSpecialName && !method.Name.StartsWith("op_", StringComparison.Ordinal);

bool IsInitOnly(MethodInfo setter) =>
    setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit");

bool IsObsolete(MemberInfo member) => member.GetCustomAttributesData().Any(a => a.AttributeType == typeof(ObsoleteAttribute));

bool TypeHasAttribute(MemberInfo member, string name) => member.GetCustomAttributesData().Any(a => a.AttributeType.Name == name);

bool ParameterHasAttribute(ParameterInfo parameter, string name) => parameter.GetCustomAttributesData().Any(a => a.AttributeType.Name == name);

static string Literal(object? value) => value switch
{
    null => "null",
    string s => $"\"{s}\"",
    bool b => b ? "true" : "false",
    _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "?",
};

static string Escape(string value) => value
    .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

// Self-contained page: it sits next to a fingerprinted Blazor stylesheet it cannot link to,
// so it carries its own. The colours, the three theme states and the storage key are copied
// from the Playground on purpose, so a theme chosen there still applies here.
string Shell(string outline, string content) => $$"""
    <!DOCTYPE html>
    <html lang="en">

    <head>
      <meta charset="utf-8" />
      <title>FeatherQR API</title>
      <meta name="description" content="Every public type in FeatherQR and FeatherQR.SkiaSharp {{version}}" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <meta name="color-scheme" id="meta-color-scheme" content="light dark" />
      <link rel="icon" type="image/svg+xml" href="../favicon.svg" />
      <script>
        (function () {
          var key = 'skqr-playground-color-mode';
          var meta = document.getElementById('meta-color-scheme');
          try {
            var v = localStorage.getItem(key);
            if (v === 'light' || v === 'dark') {
              document.documentElement.setAttribute('data-theme', v);
              if (meta) meta.setAttribute('content', v);
            }
          } catch (e) { /* private mode, cleared storage: the default is fine */ }
        })();
      </script>
      <style>
        :root {
          color-scheme: light dark;
          --accent: #d62976;
          --bg: #f6f7fb;
          --panel-bg: #ffffff;
          --panel-border: #e3e6ee;
          --text: #1f2430;
          --text-muted: #5b6474;
          --input-bg: #ffffff;
          --input-border: #c9cfdd;
          --tag-bg: #fbe7f0;
          --tag-text: #a3306a;
          --sig-bg: #f0f2f8;
          --shadow: 0 1px 3px rgba(16, 24, 40, 0.08);
        }

        @media (prefers-color-scheme: dark) {
          :root:not([data-theme="light"]) {
            --bg: #12141c;
            --panel-bg: #1a1d28;
            --panel-border: #2a2f40;
            --text: #e6e9f2;
            --text-muted: #98a1b5;
            --input-bg: #12141c;
            --input-border: #3a4157;
            --tag-bg: #3a1f2e;
            --tag-text: #f19ac2;
            --sig-bg: #222634;
            --shadow: 0 1px 3px rgba(0, 0, 0, 0.4);
          }
        }

        :root[data-theme="dark"] {
          --bg: #12141c;
          --panel-bg: #1a1d28;
          --panel-border: #2a2f40;
          --text: #e6e9f2;
          --text-muted: #98a1b5;
          --input-bg: #12141c;
          --input-border: #3a4157;
          --tag-bg: #3a1f2e;
          --tag-text: #f19ac2;
          --sig-bg: #222634;
          --shadow: 0 1px 3px rgba(0, 0, 0, 0.4);
        }

        * { box-sizing: border-box; }

        body {
          margin: 0;
          background: var(--bg);
          color: var(--text);
          font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
          line-height: 1.65;
        }

        code {
          font-family: ui-monospace, "Cascadia Mono", "SFMono-Regular", Consolas, monospace;
          font-size: 0.84rem;
        }

        .bar {
          position: sticky;
          top: 0;
          z-index: 20;
          display: flex;
          align-items: baseline;
          gap: 0.9rem;
          flex-wrap: wrap;
          padding: 0.7rem clamp(1rem, 3vw, 2rem);
          background: var(--panel-bg);
          border-bottom: 1px solid var(--panel-border);
        }
        .bar h1 { font-size: 1rem; margin: 0; }
        .back { color: var(--accent); text-decoration: none; font-size: 0.85rem; white-space: nowrap; }
        .back:hover { text-decoration: underline; }
        .meta { color: var(--text-muted); font-size: 0.8rem; font-variant-numeric: tabular-nums; }

        .layout {
          display: grid;
          grid-template-columns: minmax(0, 1fr);
          gap: 0 2rem;
          max-width: 1240px;
          margin-inline: auto;
          padding: 0 clamp(1rem, 3vw, 2rem) 4rem;
        }

        @media (min-width: 60rem) {
          .layout { grid-template-columns: 17rem minmax(0, 1fr); }
          .sidebar {
            position: sticky;
            top: 3.4rem;
            align-self: start;
            max-height: calc(100vh - 4.5rem);
            overflow-y: auto;
          }
        }

        .sidebar { padding-top: 1.5rem; }

        #q {
          width: 100%;
          padding: 0.45rem 0.7rem;
          margin-bottom: 1rem;
          background: var(--input-bg);
          color: var(--text);
          border: 1px solid var(--input-border);
          border-radius: 6px;
          font: inherit;
          font-size: 0.86rem;
        }
        #q:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }

        .toc-ns {
          margin: 1rem 0 0.4rem;
          font-size: 0.7rem;
          letter-spacing: 0.11em;
          text-transform: uppercase;
          color: var(--text-muted);
        }
        .toc-ns:first-of-type { margin-top: 0; }

        .toc-item {
          display: block;
          padding: 0.13rem 0.55rem;
          border-left: 2px solid transparent;
          color: var(--text-muted);
          text-decoration: none;
          font-size: 0.82rem;
          overflow: hidden;
          text-overflow: ellipsis;
          white-space: nowrap;
        }
        .toc-item:hover { color: var(--text); }
        .toc-item.current { border-left-color: var(--accent); color: var(--accent); }

        main { padding-top: 1.5rem; min-width: 0; }

        .ns > h2 {
          margin: 0 0 1rem;
          padding-bottom: 0.4rem;
          border-bottom: 1px solid var(--panel-border);
          font-size: 0.95rem;
          font-weight: 600;
          color: var(--text-muted);
        }

        .type {
          padding-bottom: 1.6rem;
          margin-bottom: 1.6rem;
          border-bottom: 1px solid var(--panel-border);
          scroll-margin-top: 4rem;
        }
        .type:last-child { border-bottom: 0; }
        .type[hidden], .member[hidden], .ns[hidden], .toc-item[hidden], .toc-ns[hidden] { display: none; }

        .type > h3, .member > h4 {
          margin: 0 0 0.35rem;
          font-size: 0.95rem;
          font-weight: 600;
        }
        .member > h4 { font-size: 0.88rem; }

        /* The kind is the word a reader is looking for when scanning, so it leads; the name
           carries the accent because that is what they are looking *for*. */
        .kind { color: var(--text-muted); font-weight: 400; }
        .name { color: var(--accent); }
        a.name { text-decoration: none; }
        a.name:hover { text-decoration: underline; }
        a.name:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }

        /* Signatures wrap instead of scrolling: a horizontal scrollbar per signature hides
           the end of every long one and makes the reader work for it. C# breaks cleanly at
           the spaces after commas, and the hanging indent keeps continuations distinct. */
        .sig {
          margin: 0;
          padding: 0.35rem 0.6rem 0.35rem 2.4rem;
          text-indent: -1.8rem;
          background: var(--sig-bg);
          border-radius: 4px;
          white-space: pre-wrap;
          overflow-wrap: break-word;
        }

        .anchor {
          color: var(--text-muted);
          text-decoration: none;
          margin-right: 0.45rem;
          opacity: 0;
        }
        .type:hover > h3 .anchor, .anchor:focus-visible { opacity: 1; }

        /* Doc text is prose, so it gets prose width and the body face; signatures stay mono. */
        .doc {
          margin: 0.4rem 0 0;
          max-width: 74ch;
          font-size: 0.86rem;
          color: var(--text-muted);
        }
        .type > .doc { margin-bottom: 0.2rem; }

        /* Remarks are the reasoning behind the summary, so they sit a step back from it. */
        .notes { margin-top: 0.3rem; }
        .notes > summary {
          display: inline-block;
          cursor: pointer;
          font-size: 0.74rem;
          letter-spacing: 0.05em;
          text-transform: uppercase;
          color: var(--text-muted);
          list-style: none;
        }
        .notes > summary::-webkit-details-marker { display: none; }
        .notes > summary::before { content: "\25B8"; display: inline-block; margin-right: 0.35rem; }
        .notes[open] > summary::before { content: "\25BE"; }
        .notes > summary:hover { color: var(--accent); }
        .notes > summary:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }

        .remarks {
          margin-top: 0.3rem;
          padding-left: 0.7rem;
          border-left: 1px solid var(--panel-border);
          font-size: 0.82rem;
          opacity: 0.85;
        }

        .members { margin-top: 1rem; }

        .member {
          padding: 0.5rem 0 0.6rem 0.9rem;
          border-left: 2px solid var(--panel-border);
          margin-bottom: 0.35rem;
        }
        .member:hover { border-left-color: var(--accent); }

        .tag {
          display: inline-block;
          margin-left: 0.5rem;
          padding: 0.02rem 0.4rem;
          border-radius: 999px;
          background: var(--tag-bg);
          color: var(--tag-text);
          font-size: 0.65rem;
          letter-spacing: 0.04em;
          text-transform: uppercase;
        }

        .empty { color: var(--text-muted); }
        .empty[hidden] { display: none; }

        @media (prefers-reduced-motion: no-preference) {
          html { scroll-behavior: smooth; }
        }
      </style>
    </head>

    <body>
      <header class="bar">
        <a class="back" href="../">&#8592; Playground</a>
        <h1>FeatherQR API</h1>
        <span class="meta">{{version}} &middot; <span id="count">{{model.Length}} types</span>{{(sourceCommit is null ? "" : $" &middot; source {sourceCommit}")}}</span>
      </header>

      <div class="layout">
        <aside class="sidebar">
          <input id="q" type="search" placeholder="Filter (press /)" autocomplete="off" spellcheck="false"
            aria-label="Filter types and members" />
          <nav id="toc" aria-label="Types">
    {{outline}}
          </nav>
        </aside>

        <main>
    {{content}}
          <p class="empty" id="empty" hidden>Nothing matches that.</p>
        </main>
      </div>

      <script>
        (function () {
          var box = document.getElementById('q');
          var count = document.getElementById('count');
          var empty = document.getElementById('empty');
          var types = Array.prototype.slice.call(document.querySelectorAll('.type'));
          var sections = Array.prototype.slice.call(document.querySelectorAll('.ns'));
          var tocItems = Array.prototype.slice.call(document.querySelectorAll('.toc-item'));
          var tocGroups = Array.prototype.slice.call(document.querySelectorAll('.toc-ns'));
          var notes = Array.prototype.slice.call(document.querySelectorAll('.notes'));
          var tocByType = {};
          tocItems.forEach(function (item) { tocByType[item.dataset.for] = item; });

          function apply() {
            var term = box.value.trim().toLowerCase();
            var shown = 0;

            types.forEach(function (type) {
              var nameHit = !term || type.dataset.name.indexOf(term) !== -1;
              var hit = !term || nameHit || type.dataset.text.indexOf(term) !== -1;
              type.hidden = !hit;
              if (hit) shown++;

              var entry = tocByType[type.id];
              if (entry) entry.hidden = !hit;

              // A type matched by name keeps all of its members; matched inside one member,
              // only that member.
              Array.prototype.forEach.call(type.querySelectorAll('.member'), function (member) {
                member.hidden = !!term && !nameHit && member.dataset.text.indexOf(term) === -1;
              });
            });

            sections.forEach(function (section) {
              section.hidden = !section.querySelector('.type:not([hidden])');
            });

            // Open a folded Notes block only when the term is in there, so a search never
            // leaves the reader wondering where the match was. Clearing the box folds them
            // back rather than leaving the page expanded.
            notes.forEach(function (note) {
              note.open = !!term && note.dataset.text.indexOf(term) !== -1;
            });

            // A namespace heading in the outline belongs to the links that follow it.
            tocGroups.forEach(function (group) {
              var visible = false;
              for (var n = group.nextElementSibling; n && n.classList.contains('toc-item'); n = n.nextElementSibling) {
                if (!n.hidden) { visible = true; break; }
              }
              group.hidden = !visible;
            });

            empty.hidden = shown !== 0;
            count.textContent = term ? shown + ' of ' + types.length + ' types' : types.length + ' types';
          }

          box.addEventListener('input', apply);
          box.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') { box.value = ''; apply(); }
          });
          document.addEventListener('keydown', function (e) {
            if (e.key === '/' && document.activeElement !== box) { e.preventDefault(); box.focus(); }
          });

          // Highlight the outline entry for whatever is being read. Bottom margin keeps the
          // choice on the upper part of the viewport rather than flipping at the fold.
          if ('IntersectionObserver' in window) {
            var current = null;
            var observer = new IntersectionObserver(function (entries) {
              entries.forEach(function (entry) {
                if (!entry.isIntersecting) return;
                var entryLink = tocByType[entry.target.id];
                if (!entryLink || entryLink === current) return;
                if (current) current.classList.remove('current');
                entryLink.classList.add('current');
                current = entryLink;
              });
            }, { rootMargin: '0px 0px -75% 0px' });
            types.forEach(function (type) { observer.observe(type); });
          }

          apply();
        })();
      </script>
    </body>

    </html>

    """;

static string? GetOutputPath(string[] args)
{
    var index = Array.IndexOf(args, "-o");
    if (index < 0) index = Array.IndexOf(args, "--output");
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
