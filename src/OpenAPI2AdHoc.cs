using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes; // JsonNode, JsonValue
using System.Threading.Tasks;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using org.unirail.adhoc;
using File = System.IO.File;

namespace org.unirail;

/**
 * @class OpenAPI2AdHoc
 * @brief Converts OpenAPI (Swagger) specification files (JSON or YAML) into AdHoc protocol definition files.
 *
 * This class processes OpenAPI documents to generate AdHoc protocol definitions. It parses various
 * OpenAPI components like security schemes, servers, responses, parameters, schemas, paths, and
 * operations, translating them into AdHoc protocol constructs (Packs, Fields, Attributes, Actors).
 *
 * Transformation rules
 * ─────────────────────
 * • `components`, `paths`       → no wrappers: these are words of the format, not of the API
 * • Path `/pets/{petId}`        → nested interfaces `pets { petId { … } }`: the hierarchy of the API is declared, not quoted
 * • HTTP operations            → AdHoc shorthand method signatures  (L____________, …) opId(ReqType req);
 * • Simple schema-ref body     → resolved type used directly          Pet
 * • Parameters and body        → top-level  {opId}Req  class; a reusable parameter is imported, not copied
 * • A named reusable response  → its own reply pack                   NotFound
 * • A response with no content → top-level  Code_XXX  sentinel class
 * • Callbacks, links, streams  → a full actor: the states of the call, of the replies, of the pushes
 *
 * • Object schemas, compositions → packs;  enums → enums;  `const` → constants
 * • Named numbers, strings, lists, maps → TYPEDEF aliases that carry their constraints
 * • Bounds → [MinMax] / [A] / [V];  lengths → [D];  formats → the types AdHoc has for them
 *
 * HTTP tells the replies of a call apart by the status code; AdHoc tells them apart by the pack.
 * So a status becomes the name of a pack, never a number on the wire.
 *
 * @note See https://swagger.io/docs/specification/v3_0/data-models/data-types/ for OpenAPI data types.
 */
public class OpenAPI2AdHoc{
    /// <summary>Namespace of every generated description (the family convention: <c>org.&lt;format&gt;</c>).</summary>
    const string Namespace = "org.openapi";

    static string ProjectName = "";

    // ─────────────────────────────────────────────────────────────────────────
    //  CLI:  OpenAPI2AdHoc <input file or folder> [output folder]
    // ─────────────────────────────────────────────────────────────────────────
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if( args.Length == 0 || args[0] is "-h" or "--help" or "/?" )
        {
            Console.WriteLine("""
                              OpenAPI2AdHoc - OpenAPI / Swagger specification → AdHoc protocol description

                              Usage: OpenAPI2AdHoc <input file or folder> [output folder]

                                input    an OpenAPI 3.x / Swagger 2.0 document (.json, .yaml, .yml),
                                         or a folder: every such file directly inside it is converted
                                output   the folder the <name>.cs files are written to (default: ./AdHoc)

                              Exit code: 0 - every input converted, 2 - an input failed or nothing to convert.
                              """);
            return args.Length == 0 ? 2 : 0;
        }

        var src     = Path.GetFullPath(args[0]);
        var dst_dir = Path.GetFullPath(1 < args.Length ? args[1] : "AdHoc");

        List<string> inputs;
        if( Directory.Exists(src) )
            inputs = Directory.EnumerateFiles(src)
                              .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".json" or ".yaml" or ".yml")
                              .OrderBy(f => f, StringComparer.Ordinal)
                              .ToList();
        else if( File.Exists(src) ) inputs = [src];
        else
        {
            Console.Error.WriteLine($"Not found: {src}");
            return 2;
        }

        if( inputs.Count == 0 )
        {
            Console.Error.WriteLine($"No .json / .yaml / .yml file in {src}");
            return 2;
        }

        Directory.CreateDirectory(dst_dir);

        var failed = 0;
        var taken  = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // output names already written in this run
        foreach( var input in inputs )
        {
            // petstore.json next to petstore.yaml: the second one keeps its extension in the name.
            var name = project_name(Path.GetFileNameWithoutExtension(input));
            if( !taken.Add(name) ) taken.Add(name = project_name(Path.GetFileName(input)));

            var dst_file = Path.Combine(dst_dir, name + ".cs");
            try
            {
                var problems = await convert(input, dst_file, name);
                Console.WriteLine($"{Path.GetFileName(input),-40} -> {Path.GetFileName(dst_file)}{(problems == 0 ? "" : $"   ({problems} problem(s) reported by the OpenAPI reader, see above)")}");
            }
            catch( Exception e )
            {
                failed++;
                Console.Error.WriteLine($"{Path.GetFileName(input),-40} FAILED: {e}");
            }
        }

        return failed == 0 ? 0 : 2;
    }

    /// <summary>A file name turned into the name of the project interface (and of the output file).</summary>
    static string project_name(string file_name)
    {
        var sb = new StringBuilder(file_name.Length);
        foreach( var c in file_name )
            if( char.IsAsciiLetterOrDigit(c) ) sb.Append(c);
            else if( 0 < sb.Length && sb[^1] != '_' ) sb.Append('_');

        var name = sb.ToString().Trim('_');
        if( name.Length == 0 ) name = "api";
        if( char.IsDigit(name[0]) ) name = "N" + name;
        return AdHocNames.brush(name);
    }

    /// <summary>Every converted document starts from a clean model: the converter state is static.</summary>
    static void reset()
    {
        ProjectName = "";
        synthetic_client_sends.Clear();
        synthetic_server_sends.Clear();
        pack_tags.Clear();
        pack_used.Clear();
        attributes.Clear();
        Pack.enum_registry.Clear();
        root               = new();
        root_actor         = new() { name = "", parent = null };
        Actor.operation_keys.Clear();
        root_webhook_actor = new() { name = "", parent = null };
        hosts.Clear();
        tmp.Clear();
        wide_integers = false;
    }

    // Synthetic packs the converter itself created, grouped by who transmits them.
    // Direction drives the readOnly/writeOnly cleanup: a pack the client sends must not
    // carry readOnly fields; a pack the server sends must not carry writeOnly fields.
    static readonly HashSet<Pack> synthetic_client_sends = [];
    static readonly HashSet<Pack> synthetic_server_sends = [];

    // Dashboard bookkeeping: pack full name → OpenAPI tags of the operations using it,
    // and the set of pack names actually referenced by any operation.
    static readonly Dictionary<string, SortedSet<string>> pack_tags = new();
    static readonly HashSet<string>                       pack_used = [];

    // Set when a field of the document is a 64-bit integer: the file then opens with a note on `longJS`.
    static bool wide_integers;

    // ─────────────────────────────────────────────────────────────────────────
    //  Schemas
    //
    //  What a schema turns into is decided by its shape, and every reference to it follows:
    //
    //    • an object, or a composition (`allOf`, a `oneOf` / `anyOf` of several alternatives) → a pack
    //    • an `enum` of two values or more → an enum
    //    • `const`, or an `enum` of one value → a constant: what never varies is declared, not sent
    //    • anything else that has a name of its own - a number with its bounds, a string with its
    //      length, a list, a map - → a TYPEDEF alias: the name survives, and the alias carries the
    //      constraints to every field that uses it
    // ─────────────────────────────────────────────────────────────────────────
    public enum Kind{ Composite, Enum, Constant, Typedef }

    public static Kind kind_of(IOpenApiSchema s) => is_composite(s)        ? Kind.Composite :
                                                    constant_of(s) != null ? Kind.Constant :
                                                    is_enum(s)             ? Kind.Enum :
                                                                             Kind.Typedef;

    /// <summary>True when the schema needs a pack: it has properties of its own, or is put together from other schemas.</summary>
    public static bool is_composite(IOpenApiSchema? s, int depth = 0)
    {
        if( s == null || 16 < depth ) return false; // deeper than that is a cycle of references
        if( 0 < s.Properties?.Count || 0 < s.PatternProperties?.Count || is_empty_object(s) ) return true;
        if( s.AllOf?.Any(part => is_composite(part, depth + 1)) == true ) return true;

        // `oneOf` / `anyOf`: a single real alternative is that alternative (the rest only says "nullable"),
        // several are a union.
        return 1 < alternatives(s.OneOf) + alternatives(s.AnyOf);
    }

    /// <summary>
    /// An object the document declares with no properties at all: `properties: {}`, or closed with
    /// `additionalProperties: false`. That is an empty pack - a signal - where a bare `type: object`, which
    /// says nothing about its properties, is any JSON.
    /// </summary>
    public static bool is_empty_object(IOpenApiSchema s) =>
        s.Type?.HasFlag(JsonSchemaType.Object) == true && !(0 < s.Properties?.Count) && s.AdditionalProperties == null &&
        (s.Properties != null || !s.AdditionalPropertiesAllowed) &&
        !(0 < s.PatternProperties?.Count) && !(0 < s.AllOf?.Count) && !(0 < s.OneOf?.Count) && !(0 < s.AnyOf?.Count);

    static int alternatives(IList<IOpenApiSchema>? list) => list?.Count(a => a is OpenApiSchemaReference || a.Type != JsonSchemaType.Null) ?? 0;

    /// <summary>The values of `enum` that are scalars. A `null` among them only says "nullable".</summary>
    public static List<JsonValue> enum_values(IOpenApiSchema? s) =>
        s?.Enum == null ?
            [] :
            s.Enum.OfType<JsonValue>().Where(v => v.GetValueKind() != System.Text.Json.JsonValueKind.Null && !JsonNullSentinel.IsJsonNullSentinel(v)).ToList();

    /// <summary>True when the values can be the members of an enum: names, or whole numbers - two of them at least.</summary>
    public static bool is_enum(IOpenApiSchema? s)
    {
        var values = enum_values(s);
        return 1 < values.Count && values.All(v => v.GetValueKind() == System.Text.Json.JsonValueKind.String ||
                                                   v.GetValueKind() == System.Text.Json.JsonValueKind.Number &&
                                                   long.TryParse(v.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
    }

    /// <summary>
    /// The one value a schema allows - `const`, or an `enum` of a single value - as a C# constant: its type
    /// and its literal. A value that never varies tells the receiver nothing, so it is declared in the pack
    /// and not transmitted (AdHoc rejects an enum of fewer than two members for the same reason).
    /// </summary>
    public static (string type, string literal)? constant_of(IOpenApiSchema? s)
    {
        if( s == null ) return null;
        if( s.Type is { } t && (t.HasFlag(JsonSchemaType.Array) || t.HasFlag(JsonSchemaType.Object)) ) return null;

        string text;
        bool   number, boolean;
        if( s.Const != null )
        {
            text    = s.Const.Length > 1 && s.Const[0] == '"' && s.Const[^1] == '"' ? // the JSON text of a string
                          JsonNode.Parse(s.Const)!.GetValue<string>() :
                          s.Const;
            // The reader hands `const` over as text. The type of the schema says what it is; with no type, a
            // text that reads as a number or as a boolean is one.
            var typed = s.Type.HasValue && (s.Type.Value & ~JsonSchemaType.Null) != 0;
            number  = typed ?
                          s.Type!.Value.HasFlag(JsonSchemaType.Integer) || s.Type.Value.HasFlag(JsonSchemaType.Number) :
                          double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            boolean = (!typed || s.Type!.Value.HasFlag(JsonSchemaType.Boolean)) && text.ToLowerInvariant() is "true" or "false";
            if( boolean ) text = text.ToLowerInvariant();
        }
        else
        {
            var values = enum_values(s);
            if( values.Count != 1 ) return null;
            var kind = values[0].GetValueKind();
            number  = kind == System.Text.Json.JsonValueKind.Number;
            boolean = kind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False;
            text = kind == System.Text.Json.JsonValueKind.String ?
                       values[0].GetValue<string>() :
                       values[0].ToJsonString();
        }

        if( boolean && text is "true" or "false" ) return ("bool", text);
        if( number && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) )
            return (int.MinValue <= whole && whole <= int.MaxValue ? "int" : "long", text);
        if( number && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) )
            return ("double", real.ToString("R", CultureInfo.InvariantCulture));
        return ("string", verbatim(text, out _));
    }

    /// <summary>Copies the properties of an OpenAPI object schema into a Pack as fields.</summary>
    static void add_properties(IOpenApiSchema? src, Pack dst)
    {
        if( src?.Properties == null ) return;
        foreach( var (fn, fs) in src.Properties )
        {
            // The parts of an `allOf` may repeat a property: it is one property, the first part declares it.
            var key = brush(fn, "");
            if( dst.fields.Any(f => f.key == key) ) continue;

            // A property with one possible value is a constant of the pack.
            if( constant_of(fs) is { } k )
            {
                new Field(dst, fn, k.type) { constant = k.literal }.add_comment(fs is OpenApiSchemaReference ? null : fs.Description);
                continue;
            }

            // The description of the object is the doc of its pack; a field carries its own (Field.write).
            new Field(dst, fn, fs)
            {
                optional = src.Required == null || !src.Required.Contains(fn) ||
                           (fs.Type.HasValue && fs.Type.Value.HasFlag(JsonSchemaType.Null))
            };
        }
    }

    /// <summary>
    /// Puts the structure a schema describes into a pack: its properties, what `allOf` adds, the alternatives
    /// of `oneOf` / `anyOf`, the rest of an open object.
    /// </summary>
    static void compose(IOpenApiSchema schema, Pack pack)
    {
        add_properties(schema, pack);

        // Declared properties next to `additionalProperties`: the declared ones are fields, the rest is a map.
        if( schema.AdditionalProperties != null && !pack.fields.Any(f => f.key == "additionalProperties") )
            new Field(pack, "additionalProperties",
                      new OpenApiSchema { Type = JsonSchemaType.Object, AdditionalProperties = schema.AdditionalProperties, MaxProperties = schema.MaxProperties })
                .add_comment("every other property of the object");

        foreach( var (pattern, ps) in schema.PatternProperties ?? new Dictionary<string, IOpenApiSchema>() )
            new Field(pack, "patternProperties", new OpenApiSchema { Type = JsonSchemaType.Object, AdditionalProperties = ps })
                .add_comment($"the properties whose names match {pattern}");

        // allOf: a $ref to an object is inheritance, an inline part adds its own structure.
        foreach( var part in schema.AllOf ?? [] )
            if( part is OpenApiSchemaReference parent )
            {
                if( kind_of(part) == Kind.Composite ) pack.add_inherits(GetReferencePath(parent.Reference));
            }
            else compose(part, pack);

        void union(IList<IOpenApiSchema>? alternatives, string prefix)
        {
            foreach( var ps in alternatives ?? [] )
                if( ps is OpenApiSchemaReference sh )
                    new Field(pack, $"{prefix}_{sh.Reference.Id}", ps) { optional = true };
                else if( ps.Type == JsonSchemaType.Null ) { } // `{type: null}` among the alternatives only says "nullable"
                else
                {
                    var alternative = $"{prefix}{pack.alternatives++}";
                    if( is_composite(ps) ) // an object of its own: a nested pack
                        new Field(pack, $"{prefix}_{alternative}", build(Pack.get_or_new($"{pack.path}/{alternative}"), ps)) { optional = true };
                    else // a string, a number, a list…: the alternative is the type of the field itself
                        new Field(pack, $"{prefix}_{alternative}", ps) { optional = true };
                }
        }

        // A tagged union is a pack whose alternatives are all optional: an empty one costs a single bit.
        union(schema.OneOf, "OneOf");
        union(schema.AnyOf, "AnyOf");

        if( schema.Discriminator != null )
        {
            // Keep the propertyName (identifies which field carries the type tag)
            pack.attributes += add_attribute("DiscriminatorProperty", ["Property"], [$"\"{schema.Discriminator.PropertyName}\""]);

            if( schema.Discriminator.DefaultMapping?.Reference?.Id is { } fallback )
                pack.attributes += add_attribute("DiscriminatorDefault", ["Schema"], [$"\"{fallback}\""]);

            // Tag every mapping target with its discriminator value: [DiscriminatorValue("dog")].
            // Use get_or_new so this still works when the target schema hasn't been
            // iterated yet (OpenAPI enumeration order is not guaranteed).
            //
            // The mapping is inheritance, Sub : Base, only in the `allOf` style of polymorphism,
            // where this schema holds the common fields and the targets extend it. On a
            // oneOf / anyOf union this schema IS the set of alternatives: an alternative is not
            // one more union, and inheriting it would put every alternative inside each of them.
            var is_union = 0 < schema.OneOf?.Count || 0 < schema.AnyOf?.Count;
            foreach( var (value, target) in schema.Discriminator.Mapping ?? new Dictionary<string, OpenApiSchemaReference>() )
            {
                if( target?.Reference == null ) continue;
                var sub = Pack.get_or_new(GetReferencePath(target.Reference));
                if( !is_union && sub != pack ) sub.add_inherits(pack.path);

                // One schema may be the target of several discriminators (a member of two unions):
                // the attribute repeats, but the same value is said once.
                var tag = add_attribute("DiscriminatorValue", ["Value"], [$"\"{value}\""], true);
                if( !sub.attributes.Contains(tag) ) sub.attributes += tag;
            }
        }

        if( schema.Not != null )
            pack.add_comment($"CONSTRAINT: Must NOT validate against: {schema.Not.Description ?? "anonymous sub-schema"}");
    }

    /// <summary>The doc and the metadata of a schema, on the pack (or the enum) made from it.</summary>
    static void describe(IOpenApiSchema schema, Pack pack)
    {
        if( !string.IsNullOrEmpty(schema.Title) && schema.Title != pack.key ) pack.add_comment($"TITLE: {schema.Title}");
        pack.add_comment(schema.Description);
        if( schema.ExternalDocs != null )
        {
            pack.add_comment($"External Docs: {schema.ExternalDocs.Description} {schema.ExternalDocs.Url}");
            pack.attributes += add_attribute("ExternalDocs", ["Description", "Url"], [$"\"{schema.ExternalDocs.Description}\"", $"\"{schema.ExternalDocs.Url}\""]);
        }

        if( schema.Deprecated )
            pack.attributes += add_attribute("Obsolete", ["Message"], [$"\"{(string.IsNullOrEmpty(schema.Description) ? "Deprecated" : schema.Description)}\""]);

        // Examples - AllowMultiple. `examples` (the list) wins over the single `example` when both are present.
        if( 0 < schema.Examples?.Count )
            foreach( var ex in schema.Examples )
                pack.attributes += add_attribute("Example", ["Value"], [$"\"{json_text(ex)}\""], true);
        else if( schema.Example != null )
            pack.attributes += add_attribute("Example", ["Value"], [$"\"{json_text(schema.Example)}\""], true);

        pack.attributes += extensions(schema.Extensions);
    }

    /// <summary>The `x-…` extensions as `[Extension(key, value)]`; the ones the converter reads itself are not repeated.</summary>
    static string extensions(IDictionary<string, IOpenApiExtension>? list) =>
        list == null ?
            "" :
            string.Concat(list.Where(e => e.Key is not ("x-enum-varnames" or "x-enumNames" or "x-enum-descriptions"))
                              .Select(e => add_attribute("Extension", ["Key", "Value"], [$"\"{e.Key}\"", $"\"{ext_value(e.Value)}\""], true)));

    /// <summary>Makes the pack of a composite schema. Done once: the same schema is reached from many places.</summary>
    static Pack build(Pack pack, IOpenApiSchema schema)
    {
        if( pack.built ) return pack;
        pack.built          = true;
        pack.declared_empty = is_empty_object(schema);
        describe(schema, pack);
        compose(schema, pack);
        return pack;
    }

    /// <summary>
    /// The pack a schema is when it stands for a whole message: a $ref to an object schema is that pack, as it
    /// is. Anything else - a list, a number, an enum, an inline object - has to be carried by a pack made for it.
    /// </summary>
    static Pack? message_pack(IOpenApiSchema? schema) =>
        schema is OpenApiSchemaReference r && kind_of(schema) == Kind.Composite ?
            get(GetReferencePath(r.Reference)) as Pack :
            null;

    /// <summary>
    /// Makes a pack carry what a schema describes - the pack of a request, of a reply, of a callback:
    /// a $ref to an object is inherited, raw bytes become a `File` conduit, an inline object gives its
    /// fields, and everything else (a list, a map, a number, an alias) is the single field `single`.
    /// </summary>
    static void carry(IOpenApiSchema schema, Pack dst, string single, string? media_type)
    {
        if( schema is OpenApiSchemaReference r && message_pack(schema) != null ) dst.add_inherits(GetReferencePath(r.Reference));
        else if( is_binary_schema(schema) ) add_file_conduit(dst, single, null, media_type); // streamed, not buffered
        else if( schema is not OpenApiSchemaReference && is_composite(schema) ) compose(schema, dst);
        else new Field(dst, single, schema);
    }

    /// <summary>The names of the fields a pack has: declared, imported and inherited.</summary>
    static void names_in_scope(Pack p, HashSet<string> names, HashSet<Pack> seen)
    {
        if( !seen.Add(p) ) return;
        names.UnionWith(p.fields.Select(f => f.name));
        names.UnionWith(p.imports.Select(f => f.name));
        foreach( var i in p.inherits )
            if( get(i) is Pack parent )
                names_in_scope(parent, names, seen);
    }

    /// <summary>
    /// A parameter as a field. Where it travelled in HTTP - the path, the query, a header, a cookie - is not
    /// structure in AdHoc: it is one more field of the request. The location stays as a note on the field, so
    /// a gateway for HTTP clients can be built from the description.
    /// </summary>
    static Field parameter_field(IOpenApiParameter src, Pack dst, string name)
    {
        var fld = new Field(dst, name, src.Schema ?? src.Content?.Values.FirstOrDefault()?.Schema) { optional = !src.Required, parameter = true };
        fld.add_comment(src.Description);
        if( src.Deprecated )
            fld.add_attributes(add_attribute("Obsolete", ["Message"], [$"\"{(src.Description ?? "Deprecated")}\""]));
        if( src.Example != null ) fld.add_comment($"example: {json_text(src.Example)}");
        foreach( var (label, example) in src.Examples ?? new Dictionary<string, IOpenApiExample>() )
            if( example?.Value != null )
                fld.add_comment($"example {label}: {json_text(example.Value)}");
        fld.attributes += extensions(src.Extensions);
        if( src.In is { } where ) fld.note = $"in: {where.ToString().ToLowerInvariant()}";
        return fld;
    }

    /// <summary>
    /// The name of the type generated for a field (its inline enum or inline object): the field name with
    /// another case, so the two can live in one pack. A name that has no letter to change (`RootFS`, `ID`)
    /// comes back as it is - <see cref="Pack.child"/> then qualifies the type by its owner (`ImageInspect_RootFS`).
    /// </summary>
    static string cap(string s) => s.Length == 0 ?
                                       s :
                                       char.IsUpper(s[0]) ?
                                           s[..^1] + s[^1..].ToUpper() : // already capitalized → differ by last char
                                           char.ToUpper(s[0]) + s[1..];

    // ─────────────────────────────────────────────────────────────────────────
    //  Entry point
    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Converts one OpenAPI document into one AdHoc protocol description.
    /// Returns the number of problems the OpenAPI reader reported (the document was still converted);
    /// throws when the file is not an OpenAPI document at all.
    /// </summary>
    public static async Task<int> convert(string src_file, string dst_file, string project)
    {
        reset();
        ProjectName = project;
        using var stream   = File.OpenRead(src_file);
        var       settings = new OpenApiReaderSettings { RuleSet = new ValidationRuleSet() };
        settings.AddYamlReader();

        var readResult = await OpenApiDocument.LoadAsync(stream, format: null, settings: settings);
        var openAPI = readResult.Document ??
                      throw new InvalidDataException("not an OpenAPI / Swagger document" +
                                                     string.Concat((readResult.Diagnostic?.Errors ?? []).Take(5).Select(e => $"\n    {e.Pointer} {e.Message}".TrimEnd())));
        var problems   = readResult.Diagnostic?.Errors.Count ?? 0;

        if( 0 < problems )
        {
            // The document is converted anyway; a few lines are enough to see what the reader dislikes.
            const int SHOWN = 8;
            Console.Error.WriteLine($"Problems the OpenAPI reader found in {Path.GetFileName(src_file)}:");
            foreach( var error in readResult.Diagnostic!.Errors.Select(e => $"{e.Pointer} {e.Message}".Trim()).Distinct().Take(SHOWN) )
                Console.Error.WriteLine($"    {error}");
            if( SHOWN < problems ) Console.Error.WriteLine($"    … {problems} in total");
        }

        // ── Security Schemes ──────────────────────────────────────────────────
        if( 0 < openAPI.Components?.SecuritySchemes?.Count )
            foreach( var (name, scheme) in openAPI.Components.SecuritySchemes )
            {
                var schemePack = Pack.get_or_new($"components/securitySchemes/{name}");
                schemePack.fallback = schemePack.key + "_Security";
                schemePack.comment = scheme.Description ?? "";

                // Accumulate OAuth2 scopes across all flows into a single set per scheme.
                // Emitted as an enum `{Scheme}Scope { write_pets, read_pets, ... }` with scope
                // descriptions attached as /// comments on each enum field.
                var oauthScopes = new Dictionary<string, string>();
                void AddScopes(IDictionary<string, string>? scopes)
                {
                    foreach( var (sn, sd) in scopes ?? new Dictionary<string, string>() )
                        if( !oauthScopes.ContainsKey(sn) )
                            oauthScopes[sn] = sd ?? "";
                }

                switch( scheme.Type )
                {
                    case SecuritySchemeType.OAuth2:
                    {
                        var flows = scheme.Flows;
                        if( flows == null ) break;
                        if( flows.AuthorizationCode != null )
                        {
                            schemePack.attributes += add_attribute("OAuth2AuthAuthorizationCode",
                                                                   ["AuthorizationUrl", "TokenUrl", "RefreshUrl"],
                                                                   [
                                                                       $"\"{flows.AuthorizationCode.AuthorizationUrl}\"",
                                                                       $"\"{flows.AuthorizationCode.TokenUrl}\"",
                                                                       $"\"{flows.AuthorizationCode.RefreshUrl}\""
                                                                   ]);
                            AddScopes(flows.AuthorizationCode.Scopes);
                        }

                        if( flows.Implicit != null )
                        {
                            schemePack.attributes += add_attribute("OAuth2AuthImplicit",
                                                                   ["AuthorizationUrl", "RefreshUrl"],
                                                                   [$"\"{flows.Implicit.AuthorizationUrl}\"", $"\"{flows.Implicit.RefreshUrl}\""]);
                            AddScopes(flows.Implicit.Scopes);
                        }

                        if( flows.Password != null )
                        {
                            schemePack.attributes += add_attribute("OAuth2AuthPassword",
                                                                   ["TokenUrl", "RefreshUrl"],
                                                                   [$"\"{flows.Password.TokenUrl}\"", $"\"{flows.Password.RefreshUrl}\""]);
                            AddScopes(flows.Password.Scopes);
                        }

                        if( flows.ClientCredentials != null )
                        {
                            schemePack.attributes += add_attribute("OAuth2AuthClientCredentials",
                                                                   ["TokenUrl", "RefreshUrl"],
                                                                   [
                                                                       $"\"{flows.ClientCredentials.TokenUrl}\"",
                                                                       $"\"{flows.ClientCredentials.RefreshUrl}\""
                                                                   ]);
                            AddScopes(flows.ClientCredentials.Scopes);
                        }

                        if( flows.DeviceAuthorization != null ) // OpenAPI 3.2
                        {
                            schemePack.attributes += add_attribute("OAuth2AuthDeviceAuthorization",
                                                                   ["DeviceAuthorizationUrl", "TokenUrl", "RefreshUrl"],
                                                                   [
                                                                       $"\"{flows.DeviceAuthorization.DeviceAuthorizationUrl}\"",
                                                                       $"\"{flows.DeviceAuthorization.TokenUrl}\"",
                                                                       $"\"{flows.DeviceAuthorization.RefreshUrl}\""
                                                                   ]);
                            AddScopes(flows.DeviceAuthorization.Scopes);
                        }

                        // Emit an enum of scopes if any were collected.
                        if( oauthScopes.Count > 0 )
                        {
                            var scopeEnum = Pack.get_or_new($"components/securitySchemes/{name}/{brush(name, "")}Scope");
                            scopeEnum.is_enum = true;
                            foreach( var (sn, sd) in oauthScopes )
                            {
                                var fld = new Field(scopeEnum, sn, (string)null!);
                                if( !string.IsNullOrEmpty(sd) ) fld.comment = sd;
                            }
                        }

                        break;
                    }
                    case SecuritySchemeType.ApiKey:
                        schemePack.attributes += add_attribute("ApiKeyAuth", ["Name", "In"],
                                                               [$"\"{scheme.Name}\"", $"\"{scheme.In}\""]);
                        break;
                    case SecuritySchemeType.Http:
                        if( string.Equals(scheme.Scheme, "bearer", StringComparison.OrdinalIgnoreCase) )
                            schemePack.attributes += add_attribute("BearerAuth", ["BearerFormat"],
                                                                   [$"\"{scheme.BearerFormat}\""]);
                        else if( string.Equals(scheme.Scheme, "basic", StringComparison.OrdinalIgnoreCase) )
                            schemePack.attributes += add_attribute("BasicAuth", [], []);
                        else
                            schemePack.attributes += add_attribute("HttpAuth", ["Scheme"],
                                                                   [$"\"{scheme.Scheme}\""]);
                        break;
                    case SecuritySchemeType.OpenIdConnect:
                        schemePack.attributes += add_attribute("OpenIdConnect", ["OpenIdConnectUrl"],
                                                               [$"\"{scheme.OpenIdConnectUrl}\""]);
                        break;
                    case (SecuritySchemeType)5:
                        schemePack.attributes += add_attribute("MutualTLS", [], []);
                        break;
                }

                if( scheme.Deprecated ) // OpenAPI 3.2
                    schemePack.attributes += add_attribute("Obsolete", ["Message"], [$"\"{(string.IsNullOrEmpty(scheme.Description) ? "Deprecated" : scheme.Description)}\""]);

                if( scheme.Extensions != null )
                    foreach( var (ek, ev) in scheme.Extensions )
                        schemePack.attributes +=
                            add_attribute("SecurityExtension", ["Key", "Value"], [$"\"{ek}\"", $"\"{ext_value(ev)}\""], true);
            }


        if( 0 < openAPI.Security?.Count )
        {
            // Attach as attributes on the root project interface via a synthetic pack
            var secPack = Pack.get_or_new("security/global");
            foreach( var requirement in openAPI.Security )
                foreach( var (schemeRef, scopes) in requirement )
                    secPack.attributes += add_attribute("GlobalSecurity",
                                                        ["Scheme", "Scopes"],
                                                        [
                                                            $"\"{schemeRef.Reference?.Id ?? schemeRef.Name}\"",
                                                            $"\"{string.Join(",", scopes)}\""
                                                        ], true);
        }

        // ── Servers → Hosts ───────────────────────────────────────────────────
        read_servers(openAPI.Servers);

        // ── Components / Schemas ──────────────────────────────────────────────
        // First of all the components: responses, parameters and request bodies refer to schemas, and
        // whether a $ref is a pack, an enum or an alias decides how it is used.
        if( 0 < openAPI.Components?.Schemas?.Count )
        {
            // Named enums first: a property that repeats the value list of a named enum without a $ref is
            // then typed with the named one instead of getting a copy of its own.
            foreach( var (name, schema) in openAPI.Components.Schemas )
                if( schema is not OpenApiSchemaReference && kind_of(schema) == Kind.Enum )
                    describe(schema, Pack.get_enum("components/schemas/" + name, schema, named: true));

            foreach( var (name, schema) in openAPI.Components.Schemas )
            {
                var path = "components/schemas/" + name;

                // An alias of another schema: followed wherever it is referenced, never written.
                if( schema is OpenApiSchemaReference shw )
                {
                    if( GetReferencePath(shw.Reference) == "#/" + path ) continue; // refers to itself: nothing to say
                    var p = Pack.get_or_new(path);
                    p.add_comment(shw.Reference.Description);
                    p.Reference = GetReferencePath(shw.Reference);
                    continue;
                }

                switch( kind_of(schema) )
                {
                    case Kind.Composite:
                        build(Pack.get_or_new(path), schema);
                        break;
                    case Kind.Enum: break; // made above
                    case Kind.Constant:
                        // A named value that never varies: a constant among the constants of the document.
                        var k = constant_of(schema)!.Value;
                        new Field(Pack.get_or_new("components/constants"), name, k.type) { constant = k.literal }.add_comment(schema.Description);
                        break;
                    default:
                        // A named number, string, list or map: an alias. The TYPEDEF field holds the type with
                        // its constraints, its doc and its metadata; every field typed with the alias gets them.
                        new Field(Pack.get_or_new(path), "TYPEDEF", schema);
                        break;
                }
            }
        }

        // ── Components / Responses ────────────────────────────────────────────
        if( 0 < openAPI.Components?.Responses?.Count )
            create_response_packs("components/responses", openAPI.Components.Responses, null);

        if( 0 < openAPI.Components?.Headers?.Count )
        {
            var components_headers = Pack.get_or_new("components/headers");
            foreach( var (name, header) in openAPI.Components.Headers )
            {
                var fld = new Field(components_headers, name, header.Schema ?? header.Content?.Values.FirstOrDefault()?.Schema)
                          { optional = !header.Required };
                fld.add_comment(header.Description);
                if( header.Deprecated )
                    fld.add_attributes(add_attribute("Obsolete", ["Message"], [$"\"{(header.Description ?? "Deprecated")}\""]));
                if( header.Example != null ) fld.add_comment($"example: {json_text(header.Example)}");
            }
        }

        // ── Components / Parameters ───────────────────────────────────────────
        // A reusable parameter is declared once, here; the request of every operation that refers to it
        // imports the field (`<see cref="parameters.limit"/>+`), so the declaration stays the
        // single source of truth. style, explode, allowReserved are URL-serialization artifacts - dropped.
        if( 0 < openAPI.Components?.Parameters?.Count )
        {
            var components_parameters = Pack.get_or_new("components/parameters");

            foreach( var (name, parameter) in openAPI.Components.Parameters )
                parameter_field(parameter, components_parameters, parameter.Name ?? name).key = brush(name, ""); // found by the name of the component
        }

        // Request bodies: AdHoc is a single binary format - media types are HTTP-era noise.
        // Pick one schema (preferring application/json) and collapse.
        if( 0 < openAPI.Components?.RequestBodies?.Count )
            foreach( var (name, requestBody) in openAPI.Components.RequestBodies )
            {
                var rbPack = Pack.get_or_new($"components/requestBodies/{name}");
                rbPack.fallback = rbPack.key + "_Body";
                rbPack.add_comment(requestBody.Description);
                if( requestBody.Required )
                    rbPack.attributes += add_attribute("Required", [], []);

                var picked = pick_schema(requestBody.Content, out var media, out _);
                if( picked != null ) carry(picked, rbPack, "body", media);
                else if( media != null ) add_file_conduit(rbPack, "body", null, media);
            }

        // ── Paths / Operations ────────────────────────────────────────────────
        // AdHoc has no URL. Path/query/header parameters are all just fields of the request pack.
        // Media-type content negotiation is HTTP baggage; we pick one representative schema.
        // ──────────────────────────────────────────────────────────────────────
        if( openAPI.Paths != null )
            foreach( var (path, item) in openAPI.Paths )
            {
                if( item.Operations is not { Count: > 0 } ) continue; // a path item may hold no operation at all

                // The path of an operation is where it is declared: an interface per segment of the path, the
                // operation in the innermost one. What the path item says of all its operations is the doc of it.
                var where = Actor.container(path, root_actor);
                where.add_comment(item.Summary);
                if( item.Description != item.Summary ) where.add_comment(item.Description);

                foreach( var (httpMethod, operation) in item.Operations )
                {
                    // An operation is called by its `operationId`; one without is the method of its path, and
                    // `get`. Its packs stand in the project, so they are named by a key no other operation has.
                    var named = !string.IsNullOrEmpty(operation.OperationId);
                    var actor = Actor.operation(where,
                                                named ? operation.OperationId! : httpMethod.ToString().ToLowerInvariant(),
                                                named ? operation.OperationId! : derived_name(httpMethod.ToString(), path));

                    // Its packs are declared in the project interface and named after it: `listPetsReq`, `listPets_200`.
                    read_operation(actor, httpMethod.ToString().ToUpperInvariant(), path, item, operation,
                                   request: actor.key + "Req",
                                   callbacks: actor.key,
                                   responses: actor.key,
                                   webhook: false);
                }
            }

        // ── Webhooks (OpenAPI 3.1) - server-initiated pushes on a separate connection.
        // Emitted as actors under `root_webhook_actor`, which is wired to
        // `Connects<Server, Subscriber>` in the output template. The "request" of a webhook is the
        // server's push, its responses are the subscriber's acknowledgements.
        if( openAPI.Webhooks != null )
            foreach( var (whName, whItem) in openAPI.Webhooks )
            {
                if( whItem.Operations is not { Count: > 0 } ) continue;

                foreach( var (httpMethod, operation) in whItem.Operations )
                {
                    // A webhook with one operation that has no other name is that operation, named as the document
                    // names the webhook. Otherwise the webhook is an interface and its operations are declared in it.
                    var named = !string.IsNullOrEmpty(operation.OperationId);
                    var alone = whItem.Operations.Count == 1 && (!named || brush(operation.OperationId!, "") == brush(whName, ""));
                    var where = alone ? root_webhook_actor : Actor.container(whName, root_webhook_actor);

                    var actor = Actor.operation(where,
                                                alone ? whName : named ? operation.OperationId! : httpMethod.ToString().ToLowerInvariant(),
                                                named ? operation.OperationId! : alone ? whName : whName + "_" + httpMethod.ToString().ToLowerInvariant());

                    // What the path item says of its operations: of the one operation, or of the interface that holds them.
                    if( alone || where.comment == "" )
                    {
                        (alone ? actor : where).add_comment(whItem.Summary);
                        if( whItem.Description != whItem.Summary ) (alone ? actor : where).add_comment(whItem.Description);
                    }

                    // `webhooks` keeps its packs apart from those of an operation of the same name; it is not written.
                    read_operation(actor, httpMethod.ToString().ToUpperInvariant(), whName, whItem, operation,
                                   request: $"webhooks/{actor.key}Req",
                                   callbacks: $"webhooks/{actor.key}",
                                   responses: $"webhooks/{actor.key}",
                                   webhook: true);
                }
            }


        // readOnly/writeOnly → direction projections (_Read/_Write) + cleanup of synthetic packs.
        apply_direction_semantics();

        // Force every field's type to resolve once - this lazily instantiates format TYPEDEF
        // alias Packs (Uuid, IPv4, IPv6, …) and inline-object packs before the write pass
        // starts. Without this, any pack created mid-write lands in a sibling that's already
        // been enumerated and silently gets dropped from the emitted output. Resolution can
        // itself create packs with unresolved fields → iterate to a fixpoint.
        void pre_resolve(Pack p)
        {
            foreach( var fld in p.fields.ToList() ) fld.get_type_string();
            foreach( var c in p.children.ToList() ) pre_resolve(c);
        }

        int count_packs(Pack p) => 1 + p.children.Sum(count_packs);

        for( int before = -1, after = count_packs(root); before != after; before = after, after = count_packs(root) )
            pre_resolve(root);

        // Every pack and every actor exists now: names can be settled, the deeper one of a conflict changes.
        // From here on names are final and may be written out.
        settle_names();

        // Now that every actor has been registered, resolve all deferred links.
        // The qualified prefix mirrors the generated C# namespace + interface nesting.
        ResolveAllLinks(root_actor, $"{Namespace}.{ProjectName}.ClientServerConnection");
        ResolveAllLinks(root_webhook_actor, $"{Namespace}.{ProjectName}.ServerSubscriberConnection");

        // Dashboard bookkeeping (must run before the StandardErrors substitution below, so the
        // individual error packs keep their tags).
        collect_dashboard_info(root_actor);
        collect_dashboard_info(root_webhook_actor);

        // ── Error pack de-duplication: collect error types that recur in ≥3 actors,
        //    emit a shared `StandardErrors` Pack Set, and substitute individual params
        //    with the set name in each affected actor. ───────────────────────────
        var errorTypeUsage = new Dictionary<string, int>();
        void tallyErrors(Actor n)
        {
            if( n != root_actor )
                foreach( var p in n.response )
                {
                    if( string.IsNullOrEmpty(p.httpCode) ) continue;
                    var isError = p.httpCode.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                                  (char.IsDigit(p.httpCode[0]) && int.TryParse(p.httpCode, out var c) && c >= 400);
                    if( !isError ) continue;
                    var tn = p.GetTypeName();
                    if( string.IsNullOrEmpty(tn) || tn == "NoArg" ) continue;
                    errorTypeUsage[tn] = errorTypeUsage.GetValueOrDefault(tn) + 1;
                }

            foreach( var c in n.children ) tallyErrors(c);
        }

        tallyErrors(root_actor);

        var sharedErrorTypes = errorTypeUsage
                              .Where(kv => kv.Value >= 3)
                              .Select(kv => kv.Key)
                              .OrderBy(s => s)
                              .ToList();

        // Build an emitted StandardErrors interface block (or empty if no shared errors)
        var standardErrorsDecl = "";
        if( sharedErrorTypes.Count > 0 )
        {
            // C# tuples need ≥2 elements - a single shared type is passed to _<> directly.
            var setExpr = sharedErrorTypes.Count == 1 ?
                              sharedErrorTypes[0] :
                              $"({string.Join(", ", sharedErrorTypes)})";
            standardErrorsDecl = $"    /// <summary>Shared error pack set - error schemas reused across operations.</summary>\n" +
                                 $"    public interface StandardErrors : _<{setExpr}> {{ }}\n";

            // Substitute: when an actor's response list contains a shared-error param, rewrite it
            // to reference `StandardErrors` instead, and dedupe.
            var sharedSet = new HashSet<string>(sharedErrorTypes);
            void substitute(Actor n)
            {
                if( n != root_actor )
                {
                    var seenStandardErrors = false;
                    var newResponses       = new List<Actor.Param>();
                    foreach( var p in n.response )
                        if( sharedSet.Contains(p.GetTypeName()) )
                        {
                            if( seenStandardErrors ) continue;
                            seenStandardErrors = true;
                            newResponses.Add(new Actor.Param("StandardErrors") { comment = "shared errors", httpCode = p.httpCode });
                        }
                        else { newResponses.Add(p); }

                    if( seenStandardErrors )
                    {
                        n.response.Clear();
                        n.response.AddRange(newResponses);
                    }
                }

                foreach( var c in n.children ) substitute(c);
            }

            substitute(root_actor);
        }


        // The Dashboard block - after pre_resolve, so lazily created packs are listed too.
        var dashboard = build_dashboard();

        // ── Write output ──────────────────────────────────────────────────────
        var sb = new StringBuilder();
        root.write(sb);

        var sb2 = new StringBuilder();
        root_actor.write(sb2);

        var sb3 = new StringBuilder();
        root_webhook_actor.write(sb3);
        var hasWebhooks = sb3.Length > 0;

        var tagAttributes = new StringBuilder();
        if( openAPI.Tags != null && openAPI.Tags.Count > 0 )
            foreach( var tag in openAPI.Tags )
            {
                // 1. Basic Tag Info; `summary`, `parent` and `kind` come with OpenAPI 3.2
                tagAttributes.AppendLine(add_attribute("TagDefinition",
                                                       ["Name", "Description", "Summary", "Parent", "Kind"],
                                                       [
                                                           $"\"{tag.Name}\"", $"\"{tag.Description}\"", $"\"{tag.Summary}\"",
                                                           $"\"{tag.Parent?.Reference?.Id}\"", $"\"{tag.Kind}\""
                                                       ], true));
                tagAttributes.Append(extensions(tag.Extensions));

                // 2. Tag-specific External Docs
                if( tag.ExternalDocs != null )
                    tagAttributes.AppendLine(add_attribute("TagExternalDocs",
                                                           ["TagName", "Description", "Url"],
                                                           [$"\"{tag.Name}\"", $"\"{tag.ExternalDocs.Description}\"", $"\"{tag.ExternalDocs.Url}\""], true));
            }

        var hasOperations = sb2.Length > 0;
        var info          = openAPI.Info;

        const string LANGS = """
                             ///<see cref = 'InTS'/>   implementation in TypeScript
                             ///<see cref = 'InCS'/>   implementation in C#
                             ///<see cref = 'InJAVA'/> implementation in JAVA
                             ///<see cref = 'InCPP'/>  implementation in C++
                             ///<see cref = 'InRS'/>   implementation in RUST
                             ///<see cref = 'InGO'/>   implementation in GO

                             """;

        // The pieces are written flat: tidy() lays the file out by nesting depth afterwards.
        var o = new StringBuilder();
        o.Append("// Generated by OpenAPI2AdHoc (https://github.com/AdHoc-Protocol)\n");
        o.Append($"// Sources: {Path.GetFileName(src_file)}{(string.IsNullOrEmpty(info?.Title) ? "" : $" - {one_line(info.Title)}")}{(string.IsNullOrEmpty(info?.Version) ? "" : $", version {one_line(info.Version)}")}" +
                 $" ({spec_version(readResult.Diagnostic?.SpecificationVersion)})\n");
        if( Originals.of(src_file) is { } original ) o.Append($"//          {original}\n");
        o.Append("// Re-run the converter instead of editing this file by hand.\n\n");
        o.Append("using System;\nusing org.unirail.Meta;\n\n");
        o.Append($"namespace {Namespace} {{ // replace with your company namespace\n");

        o.Append($"""
                  /**
                  Packs Inventory. One line per transmittable pack, pre-tagged with the OpenAPI tags
                  of the operations using it - route packs in branches by these tags (KeepDoc). Add
                  your own tags/emojis after the `/>`. On its first run AdHocAgent carries the tags
                  into its numbers tables, numbers the packs and gives a wire id to each pack that
                  becomes directly transmittable.

                  {dashboard}
                  */

                  """);

        // ── The API itself: what `info`, `tags` and `externalDocs` say, as attributes of the project
        if( !string.IsNullOrEmpty(info?.Title) ) o.Append(add_attribute("Title", ["Title"], [$"\"{info.Title}\""]));
        if( !string.IsNullOrEmpty(info?.Version) ) o.Append(add_attribute("Version", ["Version"], [$"\"{info.Version}\""]));
        if( !string.IsNullOrEmpty(info?.Summary) ) o.Append(add_attribute("Summary", ["Summary"], [$"\"{info.Summary}\""]));
        if( !string.IsNullOrEmpty(info?.Description) ) o.Append(add_attribute("Description", ["Description"], [$"\"{info.Description}\""]));
        if( info?.Contact != null ) o.Append(add_attribute("Contact", ["Name", "Email", "Url"], [$"\"{info.Contact.Name}\"", $"\"{info.Contact.Email}\"", $"\"{info.Contact.Url}\""]));
        if( !string.IsNullOrEmpty(info?.License?.Name) ) o.Append(add_attribute("License", ["Name", "Identifier", "Url"], [$"\"{info.License.Name}\"", $"\"{info.License.Identifier ?? ""}\"", $"\"{info.License.Url}\""]));
        if( openAPI.ExternalDocs != null ) o.Append(add_attribute("ExternalDocs", ["Description", "Url"], [$"\"{openAPI.ExternalDocs.Description}\"", $"\"{openAPI.ExternalDocs.Url}\""]));
        if( !string.IsNullOrEmpty(info?.TermsOfService?.ToString()) ) o.Append(add_attribute("TermsOfService", ["Url"], [$"\"{info.TermsOfService}\""]));
        o.Append(extensions(info?.Extensions)).Append(extensions(openAPI.Extensions));
        o.Append(tagAttributes);
        o.Append($"public interface {ProjectName} {{\n");

        // The developer's call the document cannot make, said once where every file starts.
        if( wide_integers )
            o.Append("""
                     // 64-bit integers are `long` / `ulong` here, as `format: int64` says. A JSON number goes through a
                     // double in JavaScript, so values above 2^53 never arrived there intact: if TypeScript hosts
                     // matter and the values stay below that, `longJS` / `ulongJS` are the cheaper types.
                     //
                     // The document states ranges, never distributions. Where the values of an integer cluster - a
                     // counter near its floor, a budget near its ceiling, a delta around zero - [A], [V] or [X] on
                     // that field is the most valuable edit after conversion.

                     """).Append('\n');

        o.Append("""
                 // Caps for collections that carry no explicit [D(...)] attribute. JSON APIs rarely declare bounds,
                 // so these permissive defaults replace AdHoc's own limit of 255 items/chars. Put [D(+N)] on a
                 // field whose real bound is known and tighten the rest.
                 enum _DefaultMaxLengthOf { Arrays = 65_535, Maps = 65_535, Sets = 65_535, Strings = 65_535, }

                 /// <summary>
                 /// Synthetic pack representing an empty request or response.
                 /// Used when an operation has no parameters or body.
                 /// </summary>
                 public class NoArg { }


                 """);
        o.Append(standardErrorsDecl);

        o.Append(banner("packs"));
        o.Append(sb);

        o.Append(banner("topology"));
        o.Append(hosts).Append('\n');

        // A document with webhooks only has no caller of the API; one with no operations at all (a library
        // of schemas) still needs a connection for its packs to be transmittable.
        if( hasOperations || !hasWebhooks )
        {
            o.Append("/// <summary>The caller of the API.</summary>\n").Append(LANGS).Append("struct Client : Host { }\n\n");
            o.Append("interface ClientServerConnection : Connects<Client, Server> {\n");
            if( hasOperations ) o.Append(sb2);
            else
                o.Append($"""
                          // The document declares no operations, so every pack of the project may travel in both
                          // directions and the FSM never transitions. SkipName keeps the metadata attribute classes
                          // declared in this interface out of the pack set.
                          [_____lr_____<@{ProjectName}>(SkipName: @"Attribute$")]
                          struct Start {"{ }"}

                          """);
            o.Append("}\n\n");
        }

        if( hasWebhooks )
        {
            o.Append("/// <summary>The receiver of the webhooks.</summary>\n").Append(LANGS).Append("struct Subscriber : Host { }\n\n");
            o.Append("interface ServerSubscriberConnection : Connects<Server, Subscriber> {\n").Append(sb3).Append("}\n\n");
        }

        if( 0 < attributes.Count )
        {
            o.Append(banner("OpenAPI metadata attributes"));
            o.Append("// Not read by AdHoc: they keep what the specification says and reach the generated code as constants.\n\n");
            foreach( var decl in attributes.Values ) o.Append(decl).Append("\n\n");
        }

        o.Append("}\n}\n");

        File.WriteAllText(dst_file, tidy(o.ToString()), new UTF8Encoding(false));
        return problems;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  read_operation
    //
    //  An operation is a call: one host sends the request, the other answers with one of the
    //  replies. For an operation of `paths` the Client calls and the Server answers; a webhook is
    //  the same thing turned around - the Server calls, the Subscriber answers.
    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// What the packs of an operation without an `operationId` are named after: its method and its path,
    /// `get_pets_petId` for `GET /pets/{petId}`. The operation itself is called `get` where its path puts it.
    /// </summary>
    static string derived_name(string method, string path) =>
        string.Join('_', new[] { method.ToLowerInvariant() }
                        .Concat(path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Replace("{", "").Replace("}", ""))));

    static void read_operation(Actor actor,  string method, string route, IOpenApiPathItem item, OpenApiOperation operation,
                               string request, string callbacks, string responses, bool webhook)
    {
        actor.http_method  = method;
        actor.operation_id = operation.OperationId ?? "";
        actor.route       = route;

        // Whoever calls sends the request, whoever answers sends the replies.
        var caller_sends   = webhook ? synthetic_server_sends : synthetic_client_sends;
        var answerer_sends = webhook ? synthetic_client_sends : synthetic_server_sends;

        if( !string.IsNullOrEmpty(operation.Summary) ) actor.add_comment(operation.Summary);
        if( !string.IsNullOrEmpty(operation.Description) && operation.Description != operation.Summary )
            actor.add_comment(operation.Description);

        // The path is said by where the operation is declared; what is left to say is the method. It becomes a
        // constant in the generated code, so an HTTP gateway for legacy clients can be built on top.
        actor.attributes += add_attribute("HttpMethod", ["Method"], [$"\"{method}\""]);

        // Tags: structured attribute AND appended to doc pool (for KeepDoc downstream filters)
        if( webhook ) actor.tags.Add("webhook");
        if( 0 < operation.Tags?.Count )
        {
            actor.add_comment($"tags: {string.Join(", ", operation.Tags.Select(t => t.Name))}");
            actor.tags.AddRange(operation.Tags.Where(t => !string.IsNullOrEmpty(t.Name)).Select(t => t.Name!));
            foreach( var tag in operation.Tags )
            {
                actor.attributes += add_attribute("Tag", ["Name", "Description"], [$"\"{tag.Name}\"", $"\"{tag.Description}\""], true);
                if( tag.ExternalDocs != null )
                    actor.attributes += add_attribute("TagExternalDocs",
                                                      ["TagName", "Description", "Url"],
                                                      [$"\"{tag.Name}\"", $"\"{tag.ExternalDocs.Description}\"", $"\"{tag.ExternalDocs.Url}\""], true);
            }
        }

        // Deprecation with description-derived message
        if( operation.Deprecated )
        {
            var msg = string.IsNullOrEmpty(operation.Description) ?
                          (operation.Summary ?? "Deprecated") :
                          operation.Description;
            actor.attributes += add_attribute("Obsolete", ["Message"], [$"\"{msg}\""]);
        }

        if( operation.ExternalDocs != null )
            actor.attributes += add_attribute("ExternalDocs", ["Description", "Url"],
                                              [$"\"{operation.ExternalDocs.Description}\"", $"\"{operation.ExternalDocs.Url}\""]);

        // Servers of the operation, or else of its path: other base URLs for this call alone.
        foreach( var server in operation.Servers is { Count: > 0 } own ? own : item.Servers ?? [] )
            actor.attributes += add_attribute("Server", ["Url", "Description", "Name"],
                                              [$"\"{server.Url}\"", $"\"{server.Description}\"", $"\"{server.Name}\""], true);

        // Security requirements - structured + doc pool. An empty list lifts the requirement of the API.
        if( operation.Security != null )
            if( operation.Security.Count == 0 ) actor.add_comment("security: none");
            else
            {
                var schemeNames = new HashSet<string>();
                foreach( var requirement in operation.Security )
                    foreach( var (schemeRef, scopes) in requirement )
                    {
                        var schemeName = schemeRef.Reference?.Id ?? schemeRef.Name ?? "UnknownScheme";
                        schemeNames.Add(schemeName);
                        actor.attributes += add_attribute("SecurityRequirement",
                                                          ["Scheme", "Scopes"],
                                                          [$"\"{schemeName}\"", $"\"{string.Join(",", scopes)}\""], true);
                    }

                if( schemeNames.Count > 0 )
                    actor.add_comment($"security: {string.Join(", ", schemeNames)}");
            }

        actor.attributes += extensions(operation.Extensions);

        // Callbacks - the answering side pushes to the caller on the connection that exists.
        // The URL expression (e.g. `{$request.body#/callbackUrl}`) says where HTTP would call back;
        // in AdHoc there is nowhere else to go, so it stays in the doc only.
        foreach( var (cbName, callback) in operation.Callbacks ?? new Dictionary<string, IOpenApiCallback>() )
            foreach( var (expression, cbItem) in callback.PathItems ?? [] )
                foreach( var (cbMethod, cbOp) in cbItem.Operations ?? [] )
                {
                    actor.add_comment($"callback {cbName}: {cbMethod.ToString().ToUpperInvariant()} {expression.Expression}" +
                                      $"{(string.IsNullOrEmpty(cbOp.Summary ?? cbOp.Description) ? "" : " - " + one_line(cbOp.Summary ?? cbOp.Description!))}");

                    // The push
                    var pushed = pick_schema(cbOp.RequestBody?.Content, out var push_media, out _);
                    if( message_pack(pushed) is { } direct ) actor.callbackRPacks.Add(new Actor.Param(direct));
                    else if( pushed != null )
                    {
                        var synth = Pack.get_or_new($"{callbacks}_{cbName}_Req"); // named after the operation whose callback it is
                        if( !synth.built )
                        {
                            synth.built = true;
                            synth.add_comment(cbOp.RequestBody?.Description);
                            carry(pushed, synth, "body", push_media);
                        }

                        answerer_sends.Add(synth);
                        actor.callbackRPacks.Add(new Actor.Param(synth));
                    }

                    // The acknowledgement
                    foreach( var (code, resp) in cbOp.Responses ?? [] )
                    {
                        var acked = pick_schema(resp.Content, out var ack_media, out _);
                        if( message_pack(acked) is { } ack ) actor.callbackLPacks.Add(new Actor.Param(ack));
                        else if( acked != null )
                        {
                            var synth = Pack.get_or_new($"{callbacks}_{cbName}_Resp_{code}");
                            if( !synth.built )
                            {
                                synth.built = true;
                                synth.add_comment(resp.Description);
                                carry(acked, synth, "value", ack_media);
                            }

                            caller_sends.Add(synth);
                            actor.callbackLPacks.Add(new Actor.Param(synth));
                        }
                        else if( code.Length > 0 && (char.IsDigit(code[0]) || code == "default") )
                            actor.callbackLPacks.Add(new Actor.Param(Pack.get_or_new($"Code_{code}")));
                    }
                }

        // ── The request ─────────────────────────────────────────────────────
        // Path-level parameters, then the operation's own: the operation overrides the path on (name, in).
        var parameters = new List<IOpenApiParameter>(item.Parameters ?? []);
        foreach( var p in operation.Parameters ?? [] )
        {
            parameters.RemoveAll(o => o.Name == p.Name && o.In == p.In);
            parameters.Add(p);
        }

        var body = pick_schema(operation.RequestBody?.Content, out var body_media, out var body_dropped);

        if( parameters.Count == 0 && operation.RequestBody is OpenApiRequestBodyReference shared &&
            get(GetReferencePath(shared.Reference)) is Pack shared_body )
            actor.request = new Actor.Param(shared_body); // a reusable request body, as it is
        else if( parameters.Count == 0 && message_pack(body) is { } body_pack )
            actor.request = new Actor.Param(body_pack); // a $ref to an object schema, as it is
        else if( operation.RequestBody == null && parameters.Count == 0 )
            actor.request = null; // nothing to send: NoArg
        else
        {
            // The body and the parameters, flattened into one pack.
            var req = Pack.get_or_new(request);
            req.built = true; // a pack of its own, whatever it ends up holding
            caller_sends.Add(req);
            req.add_comment(operation.RequestBody?.Description);

            // Body schema → inherit ($ref) / File conduit (raw bytes) / inline fields / single `body` field
            if( body != null ) carry(body, req, "body", body_media);
            else if( body_media != null ) add_file_conduit(req, "body", null, body_media); // a media type with no schema: raw bytes

            if( body_dropped != "" ) req.add_comment($"The body is also offered as {body_dropped}: one form is carried.");

            // All parameters (path/query/header/cookie) → fields. A reusable one is imported from
            // the `parameters` pack, unless its name is taken here already: an import cannot be renamed.
            var taken = new HashSet<string>();
            names_in_scope(req, taken, []);
            foreach( var p in parameters )
                if( p is OpenApiParameterReference reusable && get(GetReferencePath(reusable.Reference)) is Field declared &&
                    declared.parent != req && taken.Add(declared.name) )
                    req.imports.Add(declared);
                else
                    taken.Add(parameter_field(p, req, p.Name ?? "unnamed").name);

            actor.request = new Actor.Param(req);
        }

        // ── The replies ─────────────────────────────────────────────────────
        if( 0 < operation.Responses?.Count )
            create_response_packs(responses, operation.Responses, actor, server_sends: !webhook);
    }

    /// <summary>Where the pack of a reusable response is kept: `components/responses/NotFound`, `components/responses/Code_404`.</summary>
    static string response_path(string container, string name) => $"{container}/{(0 < name.Length && char.IsDigit(name[0]) ? "Code_" + name : name)}";

    // ─────────────────────────────────────────────────────────────────────────
    //  create_response_packs
    // ─────────────────────────────────────────────────────────────────────────
    /**
     * @brief Creates AdHoc response descriptors for OpenAPI Responses.
     *
     * For reusable component responses (actor == null), creates a named reply pack each.
     * For the responses of an operation (actor != null) a reply is, in this order:
     *
     *   • a $ref to a reusable response      → its pack: `NotFound`
     *   • a schema that is a $ref to an object → that pack, as it is: `Pet`
     *   • any other content                  → a pack of the operation:  `listPets_200`
     *   • no content at all                  → the project-wide sentinel  Code_XXX: the reply itself is the information
     *
     * HTTP tells replies apart by the status code; AdHoc tells them apart by the pack. Responses that
     * resolve to the same pack are therefore one reply.
     *
     * A sequential media type (server-sent events, JSON lines) is not one answer but a stream of
     * items: the items are sent while the actor stays in its reply state, and an empty pack closes it.
     */
    static void create_response_packs(string path, IDictionary<string, IOpenApiResponse> Responses, Actor? actor, bool server_sends = true)
    {
        var direction_set = server_sends ?
                                synthetic_server_sends :
                                synthetic_client_sends;

        if( actor == null )
        {
            // ── Reusable component responses ──────────────────────────────
            foreach( var (name, response) in Responses )
            {
                var p = Pack.get_or_new(response_path(path, name));
                p.fallback = p.key + "_Response";
                p.add_comment(response.Description);

                var schema = pick_schema(response.Content, out var media, out _);
                if( schema != null ) carry(schema, p, "value", media);
                else if( media != null ) add_file_conduit(p, "value", null, media); // a media type with no schema: raw bytes
            }

            return;
        }

        // ── Operation-specific responses ──────────────────────────────────
        var emitted = new HashSet<Pack>();

        foreach( var (code, response) in Responses )
        {
            Pack? reply = null; // kept as the object: the name of a pack is final only after settle_names()
            Pack? own   = null; // a pack only this operation has - eligible for header packs
            var   doc   = string.Join("\n", new[] { response.Summary, response.Description }.Where(t => !string.IsNullOrEmpty(t)).Distinct());

            // Only a success streams. A failure is one complete reply, whatever media types the document
            // lists for it (Swagger 2.0 lists the same ones for every response of an operation).
            var streams = code.StartsWith('2');

            if( response is OpenApiResponseReference named && get(response_path("components/responses", named.Reference.Id ?? "")) is Pack reusable )
                reply = reusable; // a named response is a reply of its own, whatever schema it carries
            else
            {
                var schema = pick_schema(response.Content, out var media, out var dropped, streams);
                if( message_pack(schema) is { } direct ) reply = direct;
                else if( schema != null || media != null )
                {
                    // A list, a map, a number, raw bytes or an inline object - a pack of this operation carries it
                    own = Pack.get_or_new($"{path}_{code}"); // named after its operation: `listPets_200`
                    if( !own.built )
                    {
                        own.built = true;
                        own.add_comment(response.Description);
                        if( schema != null ) carry(schema, own, "value", media);
                        else add_file_conduit(own, "value", null, media);
                    }

                    direction_set.Add(own);
                    reply = own;
                }

                if( dropped != "" ) actor.add_comment($"The reply {code} is also offered as {dropped}: one form is carried.");
            }

            // A stream of items, when the response has a sequential media type.
            var streamed = false;
            if( streams && response is not OpenApiResponseReference && stream_schema(response.Content, out var item_media) is { } item_schema )
            {
                var item = message_pack(item_schema);
                if( item == null )
                {
                    item = Pack.get_or_new($"{path}_Event_{code}");
                    if( !item.built )
                    {
                        item.built = true;
                        item.add_comment($"An item of the stream ({item_media}) the operation answers with.");
                        carry(item_schema, item, "value", item_media);
                    }

                    direction_set.Add(item);
                }

                // The same pack cannot be both an item and the complete answer: nothing would tell them apart.
                if( item != reply && actor.stream.All(s => s.pack != item) )
                {
                    actor.stream.Add(new Actor.Param(item) { comment = doc, httpCode = code });
                    streamed = true;
                }
            }

            // Nothing to carry: the status itself is the reply - one sentinel per code, shared project-wide.
            if( reply == null && !streamed ) reply = Pack.get_or_new($"Code_{code}");

            if( reply != null && emitted.Add(reply) )
            {
                actor.response.Add(new Actor.Param(reply) { comment = doc, httpCode = code });

                // ── HTTP response headers ──────────────────────────────────────
                // AdHoc packet headers accept only single, primitive, non-nullable fields, and they
                // attach per PACK. So: numeric/bool headers become a HeaderFor<> overlay - but only
                // when the response pack is op-specific (a shared $ref/sentinel pack would get the
                // header globally). Everything else is preserved as documentation on the operation.
                //
                // AdHoc keeps the header packs and their fields in project-wide lists, where every name
                // is unique. An HTTP header repeats on many responses (`X-Rate-Limit`), so it is ONE
                // header pack per header name and type, listing every response pack that carries it.
                foreach( var (hName, header) in response.Headers ?? new Dictionary<string, IOpenApiHeader>() )
                {
                    var hSchema = header.Schema;
                    var hType = hSchema?.Type == null                                       ? null :
                                hSchema.Type.Value.HasFlag(JsonSchemaType.Integer)          ? Field.integer_type(hSchema) :
                                hSchema.Type.Value.HasFlag(JsonSchemaType.Number)           ? hSchema.Format == "float" ? "float" : "double" :
                                hSchema.Type.Value.HasFlag(JsonSchemaType.Boolean)          ? "bool" :
                                                                                              null;

                    if( hType != null && own != null )
                    {
                        var headerPack = Pack.get_or_new($"responseHeaders/{hName}_{hType}");
                        if( headerPack.fields.Count == 0 )
                        {
                            headerPack.add_comment($"HTTP response header `{hName}`, parsed before the payload.");

                            // inline_type field: no schema → no [MinMax]/[A]/[V] attributes, which
                            // are forbidden on header fields (fixed wire size required).
                            new Field(headerPack, hName, hType).add_comment(header.Description);
                        }

                        if( !headerPack.HeaderFor.Contains(own) ) headerPack.HeaderFor.Add(own);
                    }
                    else // string-typed or attached to a shared pack - keep as documentation
                        actor.add_comment($"HTTP header on {code}: {hName}{(hSchema?.Type == null ? "" : $" ({hSchema.Type})")}{(string.IsNullOrEmpty(header.Description) ? "" : $" - {header.Description}")}");
                }
            }

            // ── Defer link resolution: actors may not exist yet ──────────────────
            foreach( var (linkName, link) in response.Links ?? new Dictionary<string, IOpenApiLink>() )
            {
                if( !actor.pendingLinkOpIds.ContainsKey(code) )
                    actor.pendingLinkOpIds[code] = new List<string>();

                if( !string.IsNullOrEmpty(link.OperationId) )
                    actor.pendingLinkOpIds[code].Add(link.OperationId);
                else if( !string.IsNullOrEmpty(link.OperationRef) )
                    actor.pendingLinkOpIds[code].Add("$ref:" + link.OperationRef);

                // What the link passes on is a runtime expression over this reply: HTTP glue, kept as doc.
                var passes = (link.Parameters ?? new Dictionary<string, RuntimeExpressionAnyWrapper>())
                            .Select(p => $"{p.Key} = {p.Value.Expression?.Expression ?? json_text(p.Value.Any)}").ToList();
                if( link.RequestBody != null ) passes.Add($"body = {link.RequestBody.Expression?.Expression ?? json_text(link.RequestBody.Any)}");
                actor.add_comment($"link {linkName} (after {code}) → {link.OperationId ?? link.OperationRef}" +
                                  $"{(0 < passes.Count ? ": " + string.Join(", ", passes) : "")}" +
                                  $"{(string.IsNullOrEmpty(link.Description) ? "" : " - " + one_line(link.Description))}");
            }
        }
    }
    // ─────────────────────────────────────────────────────────────────────────
    //  readOnly / writeOnly direction semantics (SSOT projections)
    //
    //  OpenAPI: readOnly fields exist only in server→client data, writeOnly only in
    //  client→server data. Mapping to AdHoc:
    //    • synthetic packs the converter owns → offending fields are simply removed;
    //    • shared schema packs → a `{Name}_Write` / `{Name}_Read` projection is generated
    //      (C# inheritance + `<see cref="Pack.field"/>-` exclusion - the AdHoc mixin model,
    //      keeping the source pack as the Single Source of Truth), and every actor param
    //      is re-pointed to the projection matching its direction.
    // ─────────────────────────────────────────────────────────────────────────


    /// <summary>Collects the pack's readOnly (or writeOnly) fields, including inherited ones, with their declaring pack.</summary>
    static void collect_direction_fields(Pack p, bool read_only, List<(Pack owner, Field f)> acc, HashSet<Pack> seen)
    {
        if( !seen.Add(p) ) return;
        foreach( var f in p.fields )
            if( read_only ? f.read_only : f.write_only )
                acc.Add((p, f));
        foreach( var i in p.inherits )
            if( get(i) is Pack b )
                collect_direction_fields(b, read_only, acc, seen);
    }

    /// <summary>
    /// Gets or creates the direction projection of a shared pack.
    /// write=true → the pack a CLIENT may send (readOnly fields blocked);
    /// write=false → the pack a SERVER sends (writeOnly fields blocked).
    /// Returns null when the pack needs no projection in that direction.
    /// </summary>
    static Pack? ensure_projection(Pack src, bool write)
    {
        if( src.is_enum || src.is_typedef || src.is_header ) return null;
        if( synthetic_client_sends.Contains(src) || synthetic_server_sends.Contains(src) ) return null; // cleaned in place

        var blocked = new List<(Pack owner, Field f)>();
        collect_direction_fields(src, read_only: write, blocked, []);
        if( blocked.Count == 0 ) return null;

        var parent   = src.parent ?? root;
        var existing = parent.children.FirstOrDefault(c => c.projection_of == (src, write));
        if( existing != null ) return existing;

        // The spec may own a schema called `Pet_Write` itself - the projection never reuses a pack of the source.
        // Its name follows the final name of the source and its doc names it: settle_names() gives both.
        var key = src.key + (write ? "_Write" : "_Read");
        for( var i = 2; parent.find_child(key) != null; i++ ) key = src.key + (write ? "_Write" : "_Read") + i;

        var proj = parent.child(key);
        proj.projection_of = (src, write);
        proj.blocked       = blocked;
        proj.add_inherits(src.path);
        return proj;
    }

    static void apply_direction_semantics()
    {
        // 1. Synthetic packs the converter owns: remove fields that must not travel their direction.
        void strip(Pack p, bool read_only)
        {
            var gone = p.fields.Where(f => read_only ? f.read_only : f.write_only).Select(f => f.name).ToList();
            if( 0 < gone.Count )
            {
                p.fields.RemoveAll(f => read_only ? f.read_only : f.write_only);
                p.add_comment($"{(read_only ? "readOnly" : "writeOnly")} fields excluded here (wrong direction): {string.Join(", ", gone)}");
            }

            // An inherited shared pack is swapped for its direction projection.
            p.inherits = p.inherits
                          .Select(path => get(path) is Pack b && ensure_projection(b, write: read_only) is { } pr ?
                                              pr.path :
                                              path)
                          .ToHashSet();
        }

        foreach( var p in synthetic_client_sends ) strip(p, read_only: true);
        foreach( var p in synthetic_server_sends ) strip(p, read_only: false);

        // 2. Actor params referencing shared packs: re-point to the direction projection.
        void subst(Actor.Param? prm, bool write)
        {
            if( prm?.target is { } pk && ensure_projection(pk, write) is { } pr ) prm.pack = pr;
        }

        void walk(Actor n, bool webhook)
        {
            subst(n.request, write: !webhook);           // main: client sends the request; webhook: the SERVER pushes it
            foreach( var r in n.response ) subst(r, write: webhook);
            foreach( var c in n.callbackRPacks ) subst(c, write: false); // server push
            foreach( var c in n.callbackLPacks ) subst(c, write: true);  // client ack
            foreach( var c in n.stream ) subst(c, write: webhook);        // the items of a streamed answer
            foreach( var c in n.children ) walk(c, webhook);
        }

        walk(root_actor, webhook: false);
        walk(root_webhook_actor, webhook: true);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Names
    //
    //  A name comes from the document and cannot always stay as it is:
    //    • a field and a type nested in the same pack cannot share a name (CS0102), and no member can
    //      have the name of its own type (CS0542);
    //    • sibling types whose names differ only by case cannot coexist - the files generated from
    //      them collide on Windows;
    //    • a type that has the name of a type of an enclosing scope hides it: inside, the name means
    //      the nested one. Path segments repeat all the time (`/teams`, `/access-control/teams`), and
    //      so do the names of inline types (`Pet.Status` next to a schema `Status`).
    //
    //  Names are settled once, when both trees are complete, walking them top-down. So of two names in
    //  conflict the DEEPER one changes, and the outer, more visible one keeps the spelling of the
    //  document. A name changes the way AdHocAgent itself renames a keyword: its lowercase letters go
    //  upper case one after another until the conflict is gone (`teams` → `Teams` → `TEams`). Only
    //  names that differ by nothing but case cannot be told apart that way, and get a number.
    //
    //  Until this pass has run nothing may capture the name of a pack or an actor as text: references
    //  are kept as objects and looked up by key.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>What the generated description itself declares in the project interface.</summary>
    static readonly string[] PROJECT_NAMES =
        ["NoArg", "StandardErrors", "Server", "Client", "Subscriber", "ClientServerConnection", "ServerSubscriberConnection"];

    /// <summary>
    /// Types the description refers to by their simple names - of `org.unirail.Meta` and of `System`.
    /// A pack called `File` or `DateTime` would capture those references wherever it is in scope.
    /// </summary>
    static readonly string[] IMPORTED_NAMES =
    [
        "Actor", "All", "Binary", "Close", "Connects", "DateTimeDef", "Duration", "Empty", "End", "FieldsInjectInto", "File",
        "HeaderFor", "Host", "IfSendingFrom", "InCPP", "InCS", "InGO", "InJAVA", "InRS", "InTS", "Map", "Modify", "Multiplex",
        "Offline", "Resumable", "Set", "Stream", "SwapHosts", "TimeSpanDef", "VirtuallyConnects", "X", "longJS", "ulongJS",
        "Attribute", "AttributeTargets", "AttributeUsage", "DateTime", "System", "TimeSpan",
    ];

    /// <summary>Contextual keywords C# does not accept as the name of a type (`class file` is CS9056).</summary>
    static readonly string[] NOT_A_TYPE_NAME = ["file", "required", "scoped", "record", "extension"];

    /// <summary>
    /// The name changed until <paramref name="taken"/> lets it through: lowercase letters go upper case, left
    /// to right; when no letter is left, a number is appended.
    /// </summary>
    static string recase(string name, Func<string, bool> taken)
    {
        if( !taken(name) ) return name;

        var chars = name.ToCharArray();
        for( var i = 0; i < chars.Length; i++ )
            if( char.IsLower(chars[i]) )
            {
                chars[i] = char.ToUpperInvariant(chars[i]);
                var candidate = new string(chars);
                if( !taken(candidate) && !AdHocNames.is_prohibited(candidate) ) return candidate;
            }

        for( var i = 2;; i++ )
            if( !taken(name + i) )
                return name + i;
    }

    static void settle_names()
    {
        var enclosing = new HashSet<string>(IMPORTED_NAMES);
        enclosing.UnionWith(NOT_A_TYPE_NAME);
        enclosing.UnionWith(PROJECT_NAMES);
        enclosing.Add(ProjectName);

        settle(root, enclosing);  // the types, top-down
        settle_fields(root, []);  // then the fields: a pack after the packs it inherits and imports from

        // AdHoc keeps the fields of all header packs in one project-wide list: the same header declared with
        // two types (`X-Limit` as int and as long) is two packs whose fields must not share a name.
        var header_fields = new HashSet<string>();
        foreach( var h in root.find_child("responseHeaders")?.children ?? [] )
            foreach( var f in h.fields )
            {
                f.name = recase(f.name, n => header_fields.Contains(n) || n == h.name);
                header_fields.Add(f.name);
            }

        // The connections are declared in the project interface, next to the top-level packs.
        enclosing.UnionWith(root.visible_children.Where(c => c.is_emitted).Select(c => c.name));
        settle(root_actor,         enclosing);
        settle(root_webhook_actor, enclosing);
    }

    /// <summary>Settles the names of the types nested in a pack. <paramref name="enclosing"/>: the type names visible from outside it.</summary>
    static void settle(Pack p, HashSet<string> enclosing)
    {
        var fields   = p.fields.Select(f => f.name).ToHashSet();
        var siblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The scopes are seen through: everything they hold is declared side by side, in the project interface.
        // There the schemas are named first - their names are the ones the document's authors chose and its
        // readers know - and a response, a parameter library or a pack of an operation yields to them.
        var emitted = p.visible_children.Where(c => c.is_emitted).ToList();
        if( p == root ) emitted = emitted.OrderBy(c => c.path.StartsWith("components/schemas/") ? 0 : 1).ToList();

        foreach( var c in emitted )
        {
            if( c.projection_of is { } of ) c.name = of.src.name + (of.write ? "_Write" : "_Read"); // follows its source

            bool taken(string n) => siblings.Contains(n) || enclosing.Contains(n) || fields.Contains(n);
            c.name = taken(c.name) && c.fallback != null && !taken(c.fallback) ? c.fallback : recase(c.name, taken);
            siblings.Add(c.name);

            if( c.projection_of is { } pr )
                c.add_comment($"{(pr.write ? "Write" : "Read")}-projection of {pr.src.name} ({(pr.write ? "client → server: readOnly" : "server → client: writeOnly")} fields blocked). " +
                              $"SSOT: the fields live in {pr.src.name}; rename/retype there and this projection follows.");
        }

        // The children are in scope for everything nested deeper; then the scope is left.
        var added = emitted.Select(c => c.name).Where(enclosing.Add).ToList();
        foreach( var c in emitted ) settle(c, enclosing);
        enclosing.ExceptWith(added);
    }

    /// <summary>
    /// Settles the names of the fields of a pack - after the packs it inherits and imports from, whose names
    /// it has to live with.
    /// </summary>
    static void settle_fields(Pack p, HashSet<Pack> done)
    {
        if( !done.Add(p) ) return;

        var inherited = new HashSet<string>();
        foreach( var i in p.inherits )
            if( get(i) is Pack parent )
            {
                settle_fields(parent, done);
                names_in_scope(parent, inherited, []);
            }

        foreach( var f in p.imports )
            if( f.parent != null )
                settle_fields(f.parent, done);

        // A field cannot have the name of its pack (a member of an enum may: `enum Status { Status }` is
        // legal), of a type nested in it, of a field imported into it, or of another field - a request
        // flattens the body and the parameters, where `path` may come both from the URL and from the query.
        //
        // A parameter also keeps clear of what the request inherits from its body: `PUT /users/{name}` with
        // a body that has a `name` carries two values, and one name would silently drop one of them. A
        // property a schema declares again over its `allOf` parent is the opposite case - one property,
        // refined - and stays as it is.
        if( !p.is_enum && !p.is_typedef )
        {
            var types = p.visible_children.Where(c => c.is_emitted).Select(c => c.name).ToHashSet();
            var seen  = p.imports.Select(f => f.name).ToHashSet();
            foreach( var f in p.fields )
            {
                f.name = recase(f.name, n => n == p.name || seen.Contains(n) || types.Contains(n) || f.parameter && inherited.Contains(n));
                seen.Add(f.name);
            }
        }

        foreach( var c in p.children.ToList() ) settle_fields(c, done);
    }

    /// <summary>
    /// Settles the names inside a connection or a segment of a path: of the segments nested in it and of its
    /// operations. <paramref name="enclosing"/>: the type names visible from outside it.
    /// </summary>
    static void settle(Actor a, HashSet<string> enclosing)
    {
        var siblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach( var c in a.children )
        {
            c.name = recase(c.name, n => n == a.name || siblings.Contains(n) || enclosing.Contains(n));
            siblings.Add(c.name);
        }

        // The children are in scope for everything nested deeper; then the scope is left.
        var added = a.children.Select(c => c.name).Where(enclosing.Add).ToList();
        foreach( var c in a.children )
            if( !c.IsOperation )
                settle(c, enclosing);
        enclosing.ExceptWith(added);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Dashboard (Packs Inventory)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Records, per pack, the OpenAPI tags of every operation that transmits it.</summary>
    static void collect_dashboard_info(Actor rootNode)
    {
        void mark(Actor n, Actor.Param? prm)
        {
            if( prm == null ) return;
            var tn = prm.GetTypeName();
            if( string.IsNullOrEmpty(tn) || tn == "NoArg" || tn == "StandardErrors" ) return;
            pack_used.Add(tn);
            if( n.tags.Count == 0 ) return;
            if( !pack_tags.TryGetValue(tn, out var set) ) pack_tags[tn] = set = new SortedSet<string>();
            foreach( var t in n.tags ) set.Add(t);
        }

        void walk(Actor n)
        {
            if( n.IsOperation )
            {
                mark(n, n.request);
                foreach( var r in n.response ) mark(n, r);
                foreach( var c in n.callbackRPacks ) mark(n, c);
                foreach( var c in n.callbackLPacks ) mark(n, c);
                foreach( var c in n.stream ) mark(n, c);
            }

            foreach( var c in n.children ) walk(c);
        }

        walk(rootNode);
    }

    /// <summary>
    /// Builds the Packs Inventory block for the top of the file: one `&lt;see cref/&gt;` line per
    /// transmittable pack, alphabetized, pre-tagged with the OpenAPI tags of the operations using
    /// it - ready for KeepDoc/tag-based routing. IDs are assigned reactively by the system later.
    /// </summary>
    static string build_dashboard()
    {
        var lines = new List<string>();

        void walk(Pack p)
        {
            foreach( var c in p.children )
            {
                if( string.IsNullOrEmpty(c.Reference) )
                {
                    var full = c.ToString();
                    if( !c.is_enum && !c.is_typedef && !c.is_header && !c.is_scope &&
                        !c.path.StartsWith("components/formats")            && // TYPEDEF/metadata aliases, not payloads
                        c.path is not ("components/parameters" or "components/headers") && // libraries of fields other packs import
                        (c.carries_data || pack_used.Contains(full)) )
                    {
                        var tags = pack_tags.TryGetValue(full, out var s) ?
                                       string.Join(" | ", s) :
                                       "";
                        // Project-name-qualified: the Dashboard doc sits on the project interface,
                        // so crefs resolve from the namespace scope (see AdhocProtocol.cs).
                        lines.Add($"        <see cref = '{ProjectName}.{full}'/>{(tags == "" ? "" : " " + xml_escape(tags).Replace("*/", "*&#47;"))}");
                    }

                    walk(c);
                }
            }
        }

        walk(root);
        lines.Add($"        <see cref = '{ProjectName}.NoArg'/>");
        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Link resolution (two-pass: first build all actors, then wire links)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Depth-first search for an actor whose name matches the given operationId.
    /// The root_actor sentinel itself is never returned.
    /// </summary>
    private static Actor? FindActorByOpId(Actor node, string opId)
    {
        if( node.IsOperation &&
            node.key.Equals(brush(opId, ""), StringComparison.OrdinalIgnoreCase) )
            return node;

        foreach( var child in node.children )
        {
            var found = FindActorByOpId(child, opId);
            if( found != null ) return found;
        }

        return null;
    }

    /// <summary>The operation an `operationRef` points at: `#/paths/{escaped path}/{method}` of this document.</summary>
    private static Actor? FindActorByRef(string pointer)
    {
        const string PATHS = "#/paths/";
        if( !pointer.StartsWith(PATHS) ) return null; // another document
        var rest = pointer[PATHS.Length..];
        var cut  = rest.LastIndexOf('/');
        if( cut <= 0 ) return null;

        var route  = Uri.UnescapeDataString(rest[..cut]).Replace("~1", "/").Replace("~0", "~");
        var method = rest[(cut + 1)..];

        Actor? find(Actor node) => node.IsOperation && node.route == route && node.http_method.Equals(method, StringComparison.OrdinalIgnoreCase) ?
                                       node :
                                       node.children.Select(find).FirstOrDefault(found => found != null);

        return find(root_actor);
    }

    /// <summary>
    /// Walk the actor tree and, for every actor that has pending link opIds,
    /// resolve them to fully-qualified C# paths, populate actor.links, and
    /// mark the target actor as forceExplicit so it always emits a referenceable
    /// Call state.
    /// </summary>
    private static void ResolveAllLinks(Actor node, string qualifiedPrefix)
    {
        if( node.pendingLinkOpIds.Count > 0 )
        {
            foreach( var (code, opIds) in node.pendingLinkOpIds )
            {
                var resolvedList = new List<string>();
                foreach( var opId in opIds )
                {
                    // An operationRef is a JSON pointer to the operation (`#/paths/~1users~1{id}/get`), an
                    // operationId names it. One that points outside this document resolves to nothing here.
                    var target = opId.StartsWith("$ref:") ?
                                     FindActorByRef(opId[5..]) :
                                     FindActorByOpId(root_actor, opId);
                    if( target != null )
                    {
                        target.forceExplicit = true; // must expose a named Call state
                        resolvedList.Add($"{qualifiedPrefix}.{target}.{Actor.CallState(target.key)}");
                    }
                }

                if( resolvedList.Count > 0 ) node.links[code] = resolvedList;
            }

            node.pendingLinkOpIds.Clear();
        }

        foreach( var child in node.children )
            ResolveAllLinks(child, qualifiedPrefix);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Attribute helpers
    // ─────────────────────────────────────────────────────────────────────────
    public static Dictionary<string, string> attributes = [];

    public static string add_attribute(string name, string[]? args_name, string[] args_values,
                                       bool   AllowMultiple = false)
    {
        // Every parameter has a default: an attribute whose values are all empty is written bare, `[BearerAuth]`.
        if( !attributes.ContainsKey(name) )
            attributes[name] = $"{(AllowMultiple ? "[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]\n" : "")}public class {name}Attribute : Attribute {{ public {name}Attribute({string.Join(", ", args_values.Select((a, i) => $"{(a.Length > 0 && a[0] == '\"' ? "string" : a.Contains('.') ? "double" : "long")} {(args_name == null || args_name.Length <= i ? $"arg{i}" : args_name[i])} = {(a.Length > 0 && a[0] == '\"' ? "\"\"" : "0")}"))}) {{ }} }}";

        // String args arrive wrapped in plain quotes; re-emit as verbatim @"..." with inner quotes doubled,
        // so values containing quotes or newlines stay valid C#.
        if( args_values.All(a => a == "\"\"" || a == "") ) return $"[{name}]\n";

        var cut  = false;
        var args = args_values.Select(a =>
                                      {
                                          if( a.Length < 2 || a[0] != '"' ) return a;
                                          var literal = verbatim(a[1..^1], out var c);
                                          cut |= c;
                                          return literal;
                                      }).ToList();

        // All the arguments or none, never a part of them.
        return $"{(cut ? CUT_NOTE : "")}[{name}({string.Join(", ", args)})]\n";
    }

    /// <summary>
    /// The longest string constant written (an attribute argument is one): a longer text is cut, and a comment
    /// above it says so.
    /// </summary>
    const int MAX_CONSTANT = 1000;

    const string CUT_NOTE = "// the text below is cut at 1000 characters, the document has all of it\n";

    /// <summary>The text as a verbatim C# string literal; <paramref name="cut"/> tells that it was too long and lost its tail.</summary>
    static string verbatim(string text, out bool cut)
    {
        text = text.Trim().Replace("\r", "");
        if( cut = MAX_CONSTANT < text.Length )
        {
            var keep = MAX_CONSTANT - 1;
            if( char.IsHighSurrogate(text[keep - 1]) ) keep--; // never split a surrogate pair
            text = text[..keep].TrimEnd() + "…";
        }

        return "@\"" + text.Replace("\"", "\"\"") + "\"";
    }

    static readonly System.Text.Json.JsonSerializerOptions compact_json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A JSON value as text: a scalar as it is, an object or an array as compact one-line JSON.</summary>
    static string json_text(JsonNode? node) => node switch
                                               {
                                                   null                                                 => "null",
                                                   _ when JsonNullSentinel.IsJsonNullSentinel(node)    => "null", // how the reader hands a JSON null over
                                                   JsonValue => node.ToString(),
                                                   _         => node.ToJsonString(compact_json)
                                               };

    /// <summary>The value of an `x-…` specification extension.</summary>
    static string ext_value(IOpenApiExtension ext) => ext is JsonNodeExtension jn ?
                                                           json_text(jn.Node) :
                                                           ext.ToString() ?? "";

    // ─────────────────────────────────────────────────────────────────────────
    //  Formats with a representation of their own
    //
    //  A format that is text in JSON only because JSON has nothing better gets the type AdHoc has
    //  for it. The aliases are made the first time a format is met and live in `components.formats`.
    //
    //    uuid, ipv6   16 bytes with a meaning → a pack of two 64-bit halves: 16 bytes on the wire, two
    //                 reads on each side, and - unlike an array - it can be an item of a list or a key
    //    ipv4         → uint;   mac → 6 bytes of a long
    //    date         → a DateTimeDef alias with a precision of one day: 3 bytes instead of 8
    //    time         → the time of a day is an elapsed time since midnight: a Duration of 24 hours
    //    duration     → Duration with its defaults: precision 1 s, max (1L << 53) - 1
    //
    //  Each carries a [Validate] hint with the canonical text form, for a GUI that edits it as text.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>A TYPEDEF alias: `class IPv4 { uint TYPEDEF; }`.</summary>
    public static string format_alias(string aliasName, string wireType, string? validationRegex, int textMaxLength)
    {
        var alias = Pack.get_or_new($"components/formats/{aliasName}");
        if( alias.fields.Count == 0 )
        {
            var fld = new Field(alias, "TYPEDEF", wireType);
            fld.attributes = emit_validate(validationRegex, textMaxLength) + fld.attributes;
        }

        return alias.ToString();
    }

    /// <summary>16 bytes as a pack of two halves: `class Uuid { ulong hi; ulong lo; }`.</summary>
    public static string halves_alias(string aliasName, string? validationRegex, int textMaxLength)
    {
        var alias = Pack.get_or_new($"components/formats/{aliasName}");
        if( alias.fields.Count == 0 )
        {
            alias.attributes += emit_validate(validationRegex, textMaxLength);
            new Field(alias, "hi", "ulong");
            new Field(alias, "lo", "ulong");
        }

        return alias.ToString();
    }

    /// <summary>
    /// An alias of one of AdHoc's time definitions: `class Date : DateTimeDef { public TimeSpan precision => … }`.
    /// The class name never repeats the name of the interface it implements (`Duration` is imported by `using`).
    /// </summary>
    public static string time_alias(string aliasName, string definition, params string[] members)
    {
        var alias = Pack.get_or_new($"components/formats/{aliasName}");
        if( alias.inherits.Count == 0 )
        {
            alias.add_inherits(definition);
            alias.members.AddRange(members);
        }

        return alias.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Pick a single representative schema from an OpenAPI Content map.
    //  AdHoc is one binary format - `application/json`, `application/xml` etc. are HTTP-era
    //  serialization labels we don't carry over. Prefer JSON, then any, else null.
    //
    //  `media` is the media type picked - also when it declares no schema at all, which means raw
    //  bytes. `dropped` names the other media types that describe something else: they are a loss,
    //  and the caller says so. The same schema under another label is not.
    // ─────────────────────────────────────────────────────────────────────────
    public static IOpenApiSchema? pick_schema(IDictionary<string, IOpenApiMediaType>? content, out string? media, out string dropped, bool streams = false)
    {
        media   = null;
        dropped = "";
        if( content == null || content.Count == 0 ) return null;

        // Where the content may be a stream (`streams`: a successful reply), a sequential media type is the
        // stream of items and not a complete content: see stream_schema(). Anywhere else it is content as any other.
        var complete = content.Where(c => !streams || !is_sequential(c.Key, c.Value)).ToList();
        if( complete.Count == 0 ) return null;

        var picked = complete.FirstOrDefault(c => c.Key.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) && c.Value?.Schema != null);
        if( picked.Key == null ) picked = complete.FirstOrDefault(c => c.Value?.Schema != null);
        if( picked.Key == null ) picked = complete[0];

        media = picked.Key;
        var schema = picked.Value?.Schema;

        bool same(IOpenApiSchema a, IOpenApiSchema? b) => ReferenceEquals(a, b) ||
                                                           a is OpenApiSchemaReference x && b is OpenApiSchemaReference y && x.Reference.Id == y.Reference.Id;

        dropped = string.Join(", ", complete.Where(c => c.Key != picked.Key && c.Value?.Schema != null && !same(c.Value.Schema, schema)).Select(c => c.Key));
        return schema;
    }

    /// <summary>
    /// A media type that is a sequence of items with no envelope - server-sent events, JSON lines, a JSON
    /// text sequence - or one that names the schema of its items (`itemSchema`, OpenAPI 3.2).
    /// </summary>
    public static bool is_sequential(string media_type, IOpenApiMediaType? content)
    {
        if( content?.ItemSchema != null ) return true;
        var t = media_type.ToLowerInvariant();
        return t.StartsWith("text/event-stream") || t.StartsWith("application/jsonl") || t.StartsWith("application/x-ndjson") ||
               t.StartsWith("application/x-jsonlines") || t.StartsWith("application/json-seq") || t.Contains("+json-seq") ||
               t.StartsWith("application/stream+json") || t.Contains(";stream=");
    }

    /// <summary>The schema of one item of a streamed response, when the response has a sequential media type.</summary>
    public static IOpenApiSchema? stream_schema(IDictionary<string, IOpenApiMediaType>? content, out string media)
    {
        media = "";
        foreach( var (type, m) in content ?? new Dictionary<string, IOpenApiMediaType>() )
        {
            if( !is_sequential(type, m) ) continue;

            // `itemSchema` is one item. `schema` is the whole content, which OpenAPI 3.2 reads as an array of
            // the items; before 3.2 it was written as the item itself.
            var schema = m.ItemSchema ?? m.Schema;
            if( m.ItemSchema == null && schema is not OpenApiSchemaReference && schema?.Type?.HasFlag(JsonSchemaType.Array) == true )
                schema = schema.Items;
            if( schema == null ) continue;

            media = type;
            return schema;
        }

        return null;
    }

    /// <summary>
    /// A value that is a `string` in AdHoc too: not an enum and not a format with a type of its own. Only
    /// such a value takes a length cap `[D(+N)]` - `maxLength` on an enum or on a date says nothing to AdHoc.
    /// </summary>
    public static bool is_text(IOpenApiSchema? s) =>
        s?.Type?.HasFlag(JsonSchemaType.String) == true && !(0 < s.Enum?.Count) &&
        s.Format is not ("date-time" or "date" or "time" or "duration" or "binary" or "byte" or "uuid" or "ipv4" or "ipv6" or "mac");

    /// <summary>`type: string, format: binary|byte` - a raw byte payload (file upload/download).</summary>
    public static bool is_binary_schema(IOpenApiSchema? s) =>
        s?.Type?.HasFlag(JsonSchemaType.String) == true && (s.Format is "binary" or "byte" || is_encoded(s));

    /// <summary>OpenAPI 3.1 says the same with JSON Schema's `contentEncoding: base64` instead of `format: byte`.</summary>
    static bool is_encoded(IOpenApiSchema s) =>
        ((s as OpenApiSchema ?? (s as OpenApiSchemaReference)?.Target as OpenApiSchema)?.ContentEncoding ?? "").ToLowerInvariant() is
        "base64" or "base64url" or "base32" or "base16";

    /// <summary>
    /// Body-level raw payload → a `File` conduit field: bytes are piped source→socket with no
    /// in-memory buffering (AdHoc Direct Transfer) instead of being crammed into a Binary array.
    /// Also fires when the media type is binary-ish (octet-stream etc.) but declares no schema.
    /// The media type is all that says what the bytes are, so it is kept on the field.
    /// </summary>
    public static Field add_file_conduit(Pack dst, string name, string? comment, string? media_type = null)
    {
        var fld = new Field(dst, name, "File");
        fld.attributes = (string.IsNullOrEmpty(media_type) ? "" : add_attribute("ContentType", ["MediaType"], [$"\"{media_type}\""])) +
                         "[S(1_000_000_000 /*TODO: set the real cap*/)] " + fld.attributes;
        fld.add_comment(comment);
        return fld;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  [Validate(...)] - GUI/form validation hint. Not wire semantics.
    //  Emitted for string `pattern` and `minLength` on regular schema fields, and for the aliases
    //  derived from text-based formats (uuid, ipv4, ipv6) where the wire type is binary but the GUI
    //  still needs to validate the text form.
    // ─────────────────────────────────────────────────────────────────────────
    public static string emit_validate(string? regex, int? maxLength, int? minLength = null)
    {
        if( string.IsNullOrEmpty(regex) && !maxLength.HasValue && !(0 < minLength) ) return "";

        if( !attributes.ContainsKey("Validate") )
            attributes["Validate"] = "[AttributeUsage(AttributeTargets.All)] public class ValidateAttribute : Attribute { public string Regex { get; set; } = \"\"; public long MinLength { get; set; } = -1; public long MaxLength { get; set; } = -1; }";

        // A pattern cut short is another pattern: one longer than MAX_CONSTANT is left out, not cut.
        var note = "";
        if( MAX_CONSTANT < regex?.Length )
        {
            note  = "// pattern left out: it is longer than 1000 characters, and a pattern cut short is another pattern\n";
            regex = null;
        }

        var parts = new List<string>();
        if( !string.IsNullOrEmpty(regex) ) parts.Add($"Regex = @\"{regex.Replace("\"", "\"\"")}\"");
        if( 0 < minLength ) parts.Add($"MinLength = {minLength}");
        if( maxLength.HasValue ) parts.Add($"MaxLength = {maxLength.Value}");
        return parts.Count == 0 ?
                   note :
                   $"{note}[Validate({string.Join(", ", parts)})]\n";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Entity (base class for Pack and Field)
    // ─────────────────────────────────────────────────────────────────────────
    public abstract class Entity{
        public Pack?  parent;
        public string name = "";
        public string key  = ""; // the name it is looked up by; `name` differs when a sibling already took it (ignoring case)

        public abstract void write(StringBuilder dst);

        public override string ToString()
        {
            void scan(Entity src, StringBuilder dst)
            {
                var up = src.parent;
                while( up is { is_scope: true } ) up = up.parent; // a scope is not written, so it is not part of a name

                if( up == null || up == root ) dst.Append(src.name);
                else
                {
                    scan(up, dst);
                    dst.Append('.').Append(src.name);
                }
            }

            scan(this, tmp.Clear());
            return tmp.ToString();
        }

        public string comment    = "";
        public string attributes = "";

        public Entity add_comment(string? comment)
        {
            if( !string.IsNullOrEmpty(comment) ) this.comment += comment + "\n";
            return this;
        }

        public Entity add_attributes(string attributes)
        {
            // [Obsolete] cannot repeat: a deprecated parameter with a deprecated schema is deprecated once.
            if( attributes.Contains("[Obsolete") && this.attributes.Contains("[Obsolete") ) return this;

            this.attributes += attributes + "\n";
            return this;
        }
    }

    // OpenAPI descriptions are CommonMark/HTML - raw '<' and '&' would corrupt the C# XML doc
    // comments the AdHoc parser reads (the doc pool for KeepDoc filters). Escape them.
    static string xml_escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;");

    const int DOC_WIDTH = 110;

    /// <summary>A doc line broken at spaces so it fits <see cref="DOC_WIDTH"/>; a word longer than that (a URL) stays whole.</summary>
    static IEnumerable<string> wrap(string line)
    {
        line = line.Trim();
        while( DOC_WIDTH < line.Length )
        {
            var cut = line.LastIndexOf(' ', DOC_WIDTH);
            if( cut <= 0 ) cut = line.IndexOf(' ', DOC_WIDTH);
            if( cut <= 0 ) break;
            yield return line[..cut];
            line = line[(cut + 1)..].TrimStart();
        }

        yield return line;
    }

    static string _comment(string? comment, string indent = "    ")
    {
        if( string.IsNullOrWhiteSpace(comment?.Trim()) || comment.Trim().StartsWith("(empty)") )
            return "";

        var lines = comment.Trim().Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).SelectMany(wrap).ToArray();
        if( lines.Length == 1 ) return $"{indent}/// <summary>{xml_escape(lines[0].Trim())}</summary>\n";

        var sb = new StringBuilder();

        sb.AppendLine($"{indent}/// <summary>");
        foreach( var line in lines )
        {
            // Avoid empty lines having trailing spaces after ///
            var trimmedLine = xml_escape(line.Trim());
            sb.AppendLine($"{indent}/// {(string.IsNullOrEmpty(trimmedLine) ? "" : trimmedLine)}");
        }

        sb.AppendLine($"{indent}/// </summary>");

        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Field
    // ─────────────────────────────────────────────────────────────────────────
    public class Field : Entity{
        public IOpenApiSchema? Schema;
        public bool            optional;
        public bool            has_Set_type;
        public bool            read_only;  // OpenAPI readOnly: present only in server→client data
        public bool            write_only; // OpenAPI writeOnly: present only in client→server data
        public bool            parameter;  // made from a parameter of an operation, not from a property of a schema
        public string?         constant;   // the literal of a constant: `public const string kind = @"circle";`
        public string          note = "";  // a trailing `// …` comment on the line of the field

        // A type given to the field directly instead of derived from its schema: a literal (`File`, `int`),
        // or a pack - kept as the object, because the name of a pack is final only after settle_names().
        string        inline_literal = "";
        public Pack?  inline_pack;
        public string inline_suffix = ""; // `[,,]` after the pack name: a list of them
        public string inline_type => inline_pack == null                               ? inline_literal :
                                     inline_pack.says_nothing && inline_suffix == "" ? "bool" : // nothing to carry but its presence
                                                                                         inline_pack + inline_suffix;

        public Field(Pack pack, string name, string inline_type)
        {
            parent         = pack;
            key            = brush(name, "");
            this.name      = key; // settle_names() keeps it apart from the name of the pack
            inline_literal = inline_type;
            pack.fields.Add(this);
        }

        public Field(Pack pack, string name, Pack type) : this(pack, name, "") => inline_pack = type;

        public Field(Pack pack, string name, IOpenApiSchema? schema)
        {
            parent    = pack;
            key       = brush(name, "");
            this.name = key; // settle_names() keeps it apart from the name of the pack
            Schema    = schema ??= new OpenApiSchema(); // no schema at all: an empty one, any JSON

            // What a $ref says comes from the schema it points to. The constraints of a named alias live in
            // the alias, and the metadata of a named type on the type: the field repeats neither.
            var by_ref    = schema is OpenApiSchemaReference;
            var via_alias = by_ref && kind_of(schema) == Kind.Typedef;

            var is_array  = schema.Type?.HasFlag(JsonSchemaType.Array) == true;
            var is_binary = is_binary_schema(schema);
            var is_map    = !is_array && schema.AdditionalProperties != null && !is_composite(schema);
            has_Set_type  = is_array && (schema.UniqueItems ?? false);

            if( !via_alias )
            {
                // ── Size caps → [D] ─────────────────────────────────────────
                // string        → [D(+chars)]           (intrinsic length)
                // binary blob   → [D(bytes)]            (Binary[,,] list cap; default 255 is far too small)
                // array         → [D(+itemChars, count)] combined element-length + item-count dims
                // Set           → [D(+count)] + [Key: …] for what the elements say
                // Map           → [D(+maxProperties)] + [Val: …] for what the values say
                //
                // A collection applies a range attribute to its elements; [Key: …] / [Val: …] aim at the
                // element of a Set and at the value of a Map.
                if( is_binary )
                    attributes += $"[D({(schema.MaxLength.HasValue ? schema.MaxLength.ToString() : "1_000_000 /*TODO: no size in the spec - set the real cap*/")})]\n";
                else if( is_array )
                {
                    var items = schema.Items;
                    var range = range_of(items, out _);
                    if( has_Set_type )
                    {
                        if( schema.MaxItems != null ) attributes += $"[D(+{schema.MaxItems})]\n";

                        var key = new List<string>();
                        if( items?.MaxLength != null && is_text(items) ) key.Add($"D(+{items.MaxLength})");
                        if( range != "" ) key.Add(range);
                        if( 0 < key.Count ) attributes += $"[Key: {string.Join(", ", key)}]\n";
                    }
                    else
                    {
                        var dims = new List<string>();
                        if( items?.MaxLength != null && is_text(items) ) dims.Add($"+{items.MaxLength}");
                        if( schema.MaxItems != null ) dims.Add(schema.MaxItems.ToString()!);
                        if( 0 < dims.Count ) attributes += $"[D({string.Join(", ", dims)})]\n";
                        if( range != "" ) attributes += $"[{range}]\n";
                    }

                    if( 0 < schema.MinItems && schema.MinItems != schema.MaxItems ) add_comment($"constraint: at least {schema.MinItems} items");
                }
                else if( is_map )
                {
                    if( schema.MaxProperties != null ) attributes += $"[D(+{schema.MaxProperties})]\n";

                    var value = schema.AdditionalProperties!;
                    var val   = new List<string>();
                    if( value.MaxLength != null && is_text(value) ) val.Add($"D(+{value.MaxLength})");
                    else if( value.Type?.HasFlag(JsonSchemaType.Array) == true && value.MaxItems != null &&
                             value.Items?.Type?.HasFlag(JsonSchemaType.Array) != true && !is_composite(value.Items) )
                        val.Add($"D({value.MaxItems})");
                    if( range_of(value, out _) is { Length: > 0 } range ) val.Add(range);
                    if( 0 < val.Count ) attributes += $"[Val: {string.Join(", ", val)}]\n";

                    if( 0 < schema.MinProperties ) add_comment($"constraint: at least {schema.MinProperties} properties");
                }
                else if( schema.MaxLength.HasValue && is_text(schema) ) attributes += $"[D(+{schema.MaxLength})]\n";

                // ── Numeric bounds ──────────────────────────────────────────
                if( !is_array && !is_map )
                {
                    var range = range_of(schema, out var unsaid);
                    if( range != "" ) attributes += $"[{range}]\n";
                    add_comment(unsaid);
                }

                if( schema.MultipleOf != null ) add_comment($"constraint: a multiple of {schema.MultipleOf.Value.ToString(CultureInfo.InvariantCulture)}");

                // pattern, minLength → [Validate(...)] - GUI/form validation hints only: AdHoc is a binary
                // protocol, and a text that is too short is not a wire error.
                if( is_text(schema) ) attributes += emit_validate(schema.Pattern, null, schema.MinLength);

                // A format AdHoc has no type for is still worth knowing: an e-mail, a URI, a password.
                if( !by_ref && !string.IsNullOrEmpty(schema.Format) && is_text(schema) )
                    attributes += add_attribute("Format", ["Name"], [$"\"{schema.Format}\""]);
            }

            if( !by_ref )
            {
                if( !string.IsNullOrEmpty(schema.Title) && schema.Title != name ) add_comment(schema.Title);

                if( schema.Default is JsonValue jValue && !JsonNullSentinel.IsJsonNullSentinel(jValue) )
                {
                    if( !OpenAPI2AdHoc.attributes.ContainsKey("Default") )
                        OpenAPI2AdHoc.attributes["Default"] = "public class DefaultAttribute : Attribute { public DefaultAttribute(double value) { } public DefaultAttribute(long value) { } public DefaultAttribute(string value) { } public DefaultAttribute(bool value) { } }";

                    switch( jValue.GetValueKind() )
                    {
                        case System.Text.Json.JsonValueKind.Number:
                            attributes += $"[Default({jValue.ToJsonString()})]\n";
                            break;
                        case System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False:
                            attributes += $"[Default({jValue.ToJsonString()})]\n";
                            break;
                        case System.Text.Json.JsonValueKind.String:
                            var literal = verbatim(jValue.GetValue<string>(), out var cut);
                            attributes += $"{(cut ? CUT_NOTE : "")}[Default({literal})]\n";
                            break;
                    }
                }

                // deprecated: use schema.Description as Obsolete message when present
                if( schema.Deprecated )
                    attributes += add_attribute("Obsolete", ["Message"], [$"\"{(string.IsNullOrEmpty(schema.Description) ? "Deprecated" : schema.Description)}\""]);

                attributes += extensions(schema.Extensions);

                // An enum of the schema itself, or of the ITEMS of an array / a Set. A named enum is reached
                // through its $ref; one value alone is not an enum (see constant_of).
                var enum_src = is_enum(schema)                                                  ? schema :
                               is_array && schema.Items is not OpenApiSchemaReference && is_enum(schema.Items) ? schema.Items :
                                                                                                                 null;
                if( enum_src != null )
                {
                    var en = Pack.get_enum(nested("Item"), enum_src);
                    if( string.IsNullOrEmpty(en.comment) && !ReferenceEquals(enum_src, schema) ) en.comment = enum_src.Description ?? "";
                    inline_pack   = en;
                    inline_suffix = ReferenceEquals(enum_src, schema) || has_Set_type ? // write() wraps the item type in Set<>
                                        "" :
                                        "[,,]"; // dynamic list of enums
                }
                else if( constant_of(schema) is { } k ) add_comment($"constant: the spec allows only {k.literal}");

                if( schema.Example != null ) add_comment($"example: {json_text(schema.Example)}");
                if( 0 < schema.Examples?.Count ) add_comment($"examples: {string.Join(", ", schema.Examples.Select(json_text))}");
            }

            // readOnly/writeOnly: markers stay on the source pack (SSOT); a later pass
            // builds direction projections (_Read/_Write) and strips these fields from
            // synthetic request/response packs.
            if( read_only  = schema.ReadOnly )  attributes += add_attribute("ReadOnly",  [], []);
            if( write_only = schema.WriteOnly ) attributes += add_attribute("WriteOnly", [], []);

            pack.fields.Add(this);
        }

        /// <summary>
        /// The C# integer of a schema. `format` names the width - the registered int32 and int64, and the int8 …
        /// uint64 family in common use; an integer with no format is 32 bits, as every OpenAPI generator reads it.
        /// </summary>
        public static string integer_type(IOpenApiSchema? s) => s?.Format switch
                                                                {
                                                                    "int8"   => "sbyte",
                                                                    "uint8"  => "byte",
                                                                    "int16"  => "short",
                                                                    "uint16" => "ushort",
                                                                    "uint32" => "uint",
                                                                    "int64"  => "long",
                                                                    "uint64" => "ulong",
                                                                    _        => "int"
                                                                };

        /// <summary>
        /// The range attribute the bounds of a numeric schema ask for, without the brackets:
        ///
        ///   both bounds       → MinMax(min, max)  the generator picks the smallest storage, down to bits
        ///   minimum only      → A(min)   a floor with rare excursions up: varint from the low end
        ///   maximum only      → V(max)   a ceiling with rare excursions down: varint from the high end
        ///
        /// Varint is for integers wider than one byte, so a one-sided bound of a byte is completed with the
        /// bound of its type, and an unsigned type always has its floor. <paramref name="unsaid"/> is what
        /// AdHoc has no attribute for: a one-sided bound of a float, a range of one value.
        /// </summary>
        public static string range_of(IOpenApiSchema? schema, out string unsaid)
        {
            unsaid = "";
            if( schema == null ) return "";
            var is_int = schema.Type?.HasFlag(JsonSchemaType.Integer) == true;
            if( !is_int && schema.Type?.HasFlag(JsonSchemaType.Number) != true ) return "";

            // OpenAPI 3.1: exclusiveMinimum/Maximum carry the number itself. Fold to inclusive for integers.
            var min = schema.Minimum;
            var max = schema.Maximum;
            if( min == null && schema.ExclusiveMinimum != null )
                min = is_int && long.TryParse(schema.ExclusiveMinimum, NumberStyles.Integer, CultureInfo.InvariantCulture, out var xm) ?
                          (xm + 1).ToString() :
                          schema.ExclusiveMinimum;
            if( max == null && schema.ExclusiveMaximum != null )
                max = is_int && long.TryParse(schema.ExclusiveMaximum, NumberStyles.Integer, CultureInfo.InvariantCulture, out var xM) ?
                          (xM - 1).ToString() :
                          schema.ExclusiveMaximum;
            if( min == null && max == null ) return "";

            // A bound has the type of its field: a double gets `[MinMax(0.0, 1.0)]`, and an integer has no use
            // for `minimum: 0.5`.
            string bound(string text, bool lower)
            {
                var whole = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
                if( !is_int )
                    return whole ?
                               text + ".0" :
                               text;
                return whole || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ?
                           text :
                           ((long)(lower ? Math.Ceiling(d) : Math.Floor(d))).ToString(CultureInfo.InvariantCulture);
            }

            if( min != null ) min = bound(min, true);
            if( max != null ) max = bound(max, false);

            // The type completes a bound the schema leaves out, where the type has one worth using.
            if( is_int )
                switch( integer_type(schema) )
                {
                    case "byte":
                        min ??= "0";
                        max ??= "255";
                        break;
                    case "sbyte":
                        min ??= "-128";
                        max ??= "127";
                        break;
                    case "ushort" or "uint" or "ulong":
                        if( max != null ) min ??= "0"; // an unsigned ceiling is a full range
                        break;
                }

            // minimum == maximum is not a range: the value is a constant.
            if( min != null && max != null &&
                double.TryParse(min, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) &&
                double.TryParse(max, NumberStyles.Float, CultureInfo.InvariantCulture, out var hi) && lo == hi )
            {
                unsaid = $"constant: the spec allows only {min}";
                return "";
            }

            if( min != null && max != null ) return $"MinMax({min}, {max})";
            if( is_int )
                return min != null ?
                           $"A({min})" : // values start at min; varint favors small values
                           $"V({max})";  // values capped at max

            unsaid = min != null ?
                         $"constraint: minimum {min}" :
                         $"constraint: maximum {max}";
            return "";
        }

        public string value  = "";
        public string source = ""; // an enum member: the value in the spec, when it could not be the name as it is

        /// <summary>The whole type of the field as it is written. Final only after settle_names().</summary>
        public string get_type_string() => Schema == null ?
                                               inline_type :
                                               full_shape([]).text;

        /// <summary>
        /// Set when the spec gives the value no usable type (`{}`, a free-form object, a `oneOf` of unrelated
        /// schemas, a broken $ref): any JSON may arrive. Such a value travels as raw bytes, and the field says so.
        /// </summary>
        public bool untyped;

        /// <summary>Set when the field is typed with a pack that has constants only: being there is all it says.</summary>
        public bool presence;

        Shape any()
        {
            untyped = true;
            return BYTES; // unknown/untyped schema - safest transportable fallback
        }

        /// <summary>
        /// A type the way AdHoc counts it: its text, how many array levels end it (`Binary[,,]` is one level
        /// already) and whether it is a Map or a Set. For a named alias, `expanded` is the type behind the name.
        ///
        /// AdHoc takes a field of the form `T`, `T[..]`, `T[..][..]`, `Set&lt;T | T[..]&gt;`, `Map&lt;K, T | T[..]&gt;`
        /// or an array of a Set or a Map - and nothing nested deeper. JSON nests as it likes (a map of lists
        /// of lists, a list of maps of maps), so the level that does not fit travels as a pack of its own
        /// with a single `value` field: <see cref="boxed"/>.
        /// </summary>
        public readonly record struct Shape(string text, int arrays = 0, bool collection = false, string? expanded = null)
        {
            public static implicit operator Shape(string named) => new(named);

            /// <summary>May be the element of a Set, or the value of a Map.</summary>
            public bool fits_slot => !collection && arrays <= 1;

            /// <summary>May be the item of a list.</summary>
            public bool fits_list => collection ?
                                         arrays == 0 :
                                         arrays <= 1;

            /// <summary>
            /// The shape to put inside a list, a Set or a Map: the type behind an alias, not its name - the
            /// constraints of the element are written on the collecting field.
            /// </summary>
            public Shape inside => expanded == null ?
                                       this :
                                       this with { text = expanded, expanded = null };
        }

        static readonly Shape BYTES = new("Binary[,,]", 1);

        /// <summary>
        /// Where a type generated for this field lives - its inline enum, its inline object, a box for a level of
        /// nesting. Next to the field, named after it: `Pet.Status` for `status`. An alias has room for its
        /// TYPEDEF field alone, so what its type needs is declared beside the alias: `Colors_Item` for `Colors`.
        /// </summary>
        string nested(string role, bool boxed = false) =>
            name == "TYPEDEF" && parent!.parent != null ?
                $"{parent.parent.path}/{parent.key}_{role}" :
                $"{parent!.path}/{stem ??= parent.claim(cap(key), this)}{(boxed ? "_" + role : "")}";

        // What the types of this field are named after. Taken once and kept: the name of the field may still
        // change when names are settled, and the types made for it before must be found again after.
        string? stem;

        /// <summary>
        /// A pack with one `value` field of the given schema, declared next to the owner of this field: the
        /// way to carry a level of nesting AdHoc has no form for.
        /// </summary>
        Shape boxed(IOpenApiSchema? of, string role)
        {
            var box = Pack.get_or_new(nested(role, boxed: true));
            if( !box.built ) // idempotent: types are resolved more than once
            {
                box.built = true;
                box.add_comment($"{(role == "Item" ? "An item" : "A value")} of `{name}`. AdHoc nests collections no deeper than a list of lists or a map of lists, so this level travels as a pack of its own.");
                new Field(box, "value", of);
            }

            return box.ToString();
        }

        /// <summary>
        /// The one schema a wrapper stands for: `allOf: [$ref]` is the OpenAPI 3.0 way to put a description or
        /// `nullable` next to a $ref, `oneOf` / `anyOf: [X, {type: null}]` is the 3.1 nullable. Null when the
        /// schema is not such a wrapper.
        /// </summary>
        static IOpenApiSchema? wrapped(IOpenApiSchema s)
        {
            if( 0 < s.Properties?.Count || s.AdditionalProperties != null ) return null;

            // What is really inside: a part of `allOf` that says something (not a bare description), an
            // alternative of `oneOf` / `anyOf` that is not `{type: null}`. One of them in all: a wrapper.
            var inside = (s.AllOf ?? []).Where(x => x is OpenApiSchemaReference || x.Type != null || is_composite(x))
                                        .Concat((s.OneOf ?? []).Concat(s.AnyOf ?? []).Where(x => x is OpenApiSchemaReference || x.Type != JsonSchemaType.Null))
                                        .ToList();
            return inside.Count == 1 ?
                       inside[0] :
                       null;
        }

        /// <summary>
        /// The shape of the field itself: what its schema says - as a Set when its items are unique.
        /// </summary>
        Shape full_shape(HashSet<IOpenApiSchema> visited)
        {
            if( !has_Set_type ) return shape_of(Schema, visited);

            // uniqueItems: the SET replaces the array - its element type is the array's item type
            var item = inline_pack != null    ? new Shape(inline_pack.ToString()) :
                       Schema!.Items != null  ? shape_of(Schema.Items, visited).inside :
                                                shape_of(Schema, visited).inside;
            // AdHoc has no Set of bool (two values need no set): unique booleans stay a list.
            if( item.text == "bool" ) return new Shape("bool[,,]", 1);

            return new Shape($"Set<{(item.fits_slot ? item : boxed(Schema!.Items ?? Schema, "Item")).text}>", 0, true);
        }

        Shape shape_of(IOpenApiSchema? schema, HashSet<IOpenApiSchema> visited)
        {
            if( schema == null ) return any();
            if( !visited.Add(schema) )
                return schema is OpenApiSchemaReference sh && get(GetReferencePath(sh.Reference)) is Pack { is_typedef: false, is_enum: false } pk ?
                           pk.ToString() :
                           any();
            try
            {
                if( inline_pack != null ) return new Shape(inline_type, inline_suffix == "" ? 0 : 1);

                if( schema is OpenApiSchemaReference sh2 )
                    switch( get(GetReferencePath(sh2.Reference)) )
                    {
                        // A named constant where a value is needed: the plain type of the constant.
                        case Field named: return named.inline_type;

                        // A named alias: the field is typed with its name.
                        case Pack { is_typedef: true } alias:
                            var aliased = alias.fields[0].Schema == null ?
                                              new Shape(alias.fields[0].inline_type) :
                                              alias.fields[0].full_shape(visited);
                            return aliased with { text = alias.ToString(), expanded = aliased.expanded ?? aliased.text };

                        case Pack pk:
                            // A pack with constants only says nothing but that it is there, and AdHoc would
                            // turn such a field into a `bool` itself; `{}` is any JSON.
                            if( pk.says_nothing )
                            {
                                presence = true;
                                return "bool";
                            }

                            return pk.is_empty ?
                                       any() :
                                       pk.ToString();
                        case null:
                            Console.Error.WriteLine($"    unresolved $ref {GetReferencePath(sh2.Reference)} (field {parent}.{name}): carried as raw bytes");
                            return any();
                    }

                if( (!schema.Type.HasValue || schema.Type.Value.HasFlag(JsonSchemaType.Object)) && wrapped(schema) is { } inner )
                    return shape_of(inner, visited);

                // An inline (anonymous) object or composition - a pack of its own, nested next to the owning
                // pack, instead of degrading to raw bytes.
                if( is_composite(schema) )
                {
                    var p = build(Pack.get_or_new(nested("Item")), schema);
                    if( p.says_nothing )
                    {
                        presence = true;
                        return "bool";
                    }

                    return p.is_empty ? // a composition that came to nothing: `allOf` of things that are not objects
                               any() :
                               p.ToString();
                }

                if( !schema.Type.HasValue ) return any();

                // `type: [string, null]` is a nullable string. Several real types at once leave nothing to be
                // sure of - except a number that may be whole, which is a number.
                var t = schema.Type.Value & ~JsonSchemaType.Null;
                if( t == (JsonSchemaType.Integer | JsonSchemaType.Number) ) t = JsonSchemaType.Number;
                if( ((int)t & ((int)t - 1)) != 0 ) return any();

                switch( t )
                {
                    case JsonSchemaType.Integer:
                        var integer = integer_type(schema);
                        wide_integers |= integer is "long" or "ulong";
                        return integer;
                    case JsonSchemaType.Number:
                        return schema.Format == "float" ?
                                   "float" :
                                   "double";
                    case JsonSchemaType.Boolean: return "bool";
                    case JsonSchemaType.String:
                        if( is_binary_schema(schema) ) return BYTES; // raw in-memory blob; the [D] cap is emitted by the ctor
                        switch( schema.Format )
                        {
                            case "date-time": return "DateTime";
                            case "date": return time_alias("Date", "DateTimeDef", "public TimeSpan precision => TimeSpan.FromDays(1);");
                            case "time":
                                return time_alias("TimeOfDay", "Duration", "public long     max       => 86_399_999; // the last millisecond of a day",
                                                  "public TimeSpan precision => TimeSpan.FromMilliseconds(1);");
                            case "duration": return time_alias("DurationDefault", "Duration");
                            case "uuid":
                                return halves_alias("Uuid", @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", 36);
                            case "ipv4":
                                return format_alias("IPv4", "uint", @"^(25[0-5]|2[0-4]\d|[01]?\d\d?)(\.(25[0-5]|2[0-4]\d|[01]?\d\d?)){3}$", 15);
                            case "ipv6": return halves_alias("IPv6", null, 39);
                            case "mac":
                                return format_alias("Mac", "[MinMax(0, 281_474_976_710_655)] long", @"^([0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$", 17);
                            default: return "string";
                        }
                    case JsonSchemaType.Array:
                    {
                        // JSON arrays are variable-length → dynamic list `[,,]`.
                        // Constant-length `[]` only when minItems == maxItems pins the size.
                        var const_size = schema.MinItems != null && schema.MinItems == schema.MaxItems;
                        var item       = shape_of(schema.Items, visited).inside;
                        if( !item.fits_list ) item = boxed(schema.Items, "Item");
                        var nullable = schema.Items?.Type?.HasFlag(JsonSchemaType.Null) == true ? // the items may be null, not only the list
                                           "?" :
                                           "";
                        return new Shape($"{item.text}{nullable}[{(const_size ? "" : ",,")}]", item.arrays + 1, item.collection);
                    }
                    case JsonSchemaType.Object:
                        if( schema.AdditionalProperties != null )
                        {
                            var value = shape_of(schema.AdditionalProperties, visited).inside;
                            return new Shape($"Map<string, {(value.fits_slot ? value : boxed(schema.AdditionalProperties, "Value")).text}>", 0, true);
                        }

                        break; // a free-form object: any JSON
                }

                return any();
            }
            finally { visited.Remove(schema); }
        }

        public override void write(StringBuilder dst)
        {
            if( inline_pack is { says_nothing: true } && inline_suffix == "" ) presence = true;

            // What the field says after its `;` - the things AdHoc has no place for
            var notes = string.Join("; ", new[]
                                          {
                                              note,
                                              untyped ? "any JSON in the spec: carried as raw bytes" : "",
                                              presence ? "typed with a pack that carries no value: being there is all it says" : ""
                                          }.Where(n => n != ""));
            if( notes != "" ) notes = " // " + notes;

            if( constant != null ) // a value that never varies: declared, not transmitted
            {
                dst.Append($"\n{_comment(comment)}public const {inline_type} {name} = {constant};{notes}\n");
                return;
            }

            if( Schema == null )
            {
                // A field with an explicit type: an alias (TYPEDEF), a header, a conduit
                if( !string.IsNullOrEmpty(inline_type) )
                {
                    if( !string.IsNullOrWhiteSpace(comment) || attributes != "" ) dst.AppendLine();
                    dst.Append($"{_comment(comment)}{attributes}{inline_type}{(optional ? "?" : "")} {name};{notes}\n");
                    return;
                }

                // Enum value
                dst.Append(_comment(comment));
                dst.Append(name);
                if( value != "" ) dst.Append(" = ").Append(value);
                dst.Append(',');
                if( source != "" && value == "" ) dst.Append(" // ").Append(one_line(source)); // what the spec calls it
                dst.AppendLine();
                return;
            }

            // Evaluated before the notes are final: resolving the type is what finds out that it is untyped.
            var T = get_type_string();
            notes = string.Join("; ", new[]
                                      {
                                          note,
                                          untyped ? "any JSON in the spec: carried as raw bytes" : "",
                                          presence ? "typed with a pack that carries no value: being there is all it says" : ""
                                      }.Where(n => n != ""));
            if( notes != "" ) notes = " // " + notes;

            // A $ref speaks for the schema it points to; only what is written next to the $ref is the field's own.
            var description = Schema is OpenApiSchemaReference r ?
                                  r.Reference.Description :
                                  Schema.Description;

            // A field that carries a doc or attributes stands apart; plain fields stay in one compact block.
            var doc = _comment(comment + "\n" + description);
            if( doc != "" || attributes != "" ) dst.AppendLine();
            dst.Append($"{doc}{attributes}{T}{(optional ? "?" : "")} {name};{notes}\n");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Pack
    // ─────────────────────────────────────────────────────────────────────────
    public class Pack : Entity{
        public List<Field>     fields    = [];
        public List<Pack>      children  = [];
        public HashSet<string> inherits  = [];
        public string          Reference = "";
        public bool            is_enum;
        public bool            built;        // the structure of its schema is in: a schema is reached from many places
        public int             alternatives; // inline alternatives of `oneOf` / `anyOf` numbered so far
        public List<Pack>      HeaderFor = []; // a header pack: the packs it precedes on the wire
        public bool            is_header => 0 < HeaderFor.Count;

        // Members written as they are: the properties of a time definition (`public TimeSpan precision => …;`).
        public List<string> members = [];

        // The stems the fields of this pack name their own types after (`Status` for `status`): one per field,
        // even when two fields are called the same - a `path` parameter of the URL and one of the query.
        readonly Dictionary<string, Field> stems = new();

        public string claim(string stem, Field owner)
        {
            var mine = stem;
            for( var i = 2; stems.TryGetValue(mine, out var other) && other != owner; i++ ) mine = stem + i;
            stems[mine] = owner;
            return mine;
        }

        // Fields declared in another pack and imported here: `/// <see cref="parameters.limit"/>+`.
        // The declaration stays the single source of truth: rename or retype it there and every pack that
        // imports it follows.
        public List<Field> imports = [];

        // The fields a direction projection blocks, with the pack declaring each. They are emitted as verbatim
        // `/// <see cref="Pack.field"/>-` lines (SSOT exclusion refs), never wrapped in <summary>.
        public List<(Pack owner, Field f)> blocked = [];
        public string raw_doc => string.Concat(imports.Select(f => $"/// <see cref=\"{f.parent}.{f.name}\"/>+\n")) +
                                 string.Concat(blocked.Select(t => $"/// <see cref=\"{t.owner}.{t.f.name}\"/>-\n"));

        public bool is_typedef => fields.Count == 1 && fields[0].name == "TYPEDEF";

        /// <summary>
        /// A parent is said once, however it was reached: `allOf` names it by reference (`#/components/…`) and the
        /// discriminator of the parent names it by the path of the pack.
        /// </summary>
        public void add_inherits(string path) => inherits.Add(path.StartsWith('#') ? string.Join('/', segments(path).Select(n => brush(n, ""))) : path);

        public static readonly Dictionary<string, Pack> enum_registry = new();

        /// <summary>
        /// The enum of a schema. An inline enum that repeats the value list of one made before is that one,
        /// not a copy per field (every `status` of an API); a named enum (<paramref name="named"/>) is always
        /// its own, so that a $ref to it finds it.
        ///
        /// The members are named by `x-enum-varnames` / `x-enumNames` and documented by `x-enum-descriptions`
        /// when the schema has these extensions of the common generators; otherwise by the values themselves.
        /// </summary>
        public static Pack get_enum(string ref_path, IOpenApiSchema src, bool named = false)
        {
            var values    = enum_values(src);
            var signature = string.Join("\u0001", values.Select(n => n.ToString()));
            if( !named && enum_registry.TryGetValue(signature, out var existing) ) return existing;

            var en = get_or_new(ref_path);
            if( en.is_enum ) return en; // made already
            enum_registry.TryAdd(signature, en);
            en.is_enum = true;

            List<string> list(string extension) =>
                src.Extensions != null && src.Extensions.TryGetValue(extension, out var e) && e is JsonNodeExtension { Node: JsonArray a } ?
                    a.Select(n => n?.ToString() ?? "").ToList() :
                    [];

            var names = list("x-enum-varnames");
            if( names.Count == 0 ) names = list("x-enumNames");
            var docs = list("x-enum-descriptions");

            // The kind of the JSON value decides, not the CLR type behind it: a YAML reader hands an integer
            // over as `int`, a JSON reader as a JsonElement, and TryGetValue<long> accepts only one of them.
            for( var i = 0; i < values.Count; i++ )
            {
                var jv = values[i];
                var text = jv.GetValueKind() == System.Text.Json.JsonValueKind.String ?
                               jv.GetValue<string>() :
                               jv.ToJsonString(); // 0, 1.5, true
                var label = i < names.Count && names[i] != ""                       ? names[i] :
                            jv.GetValueKind() == System.Text.Json.JsonValueKind.String ? text :
                                                                                         "x" + text;
                var member = new Field(en, label, (string)null!);
                if( i < docs.Count ) member.add_comment(docs[i]);

                // An enum is integral: a whole number is the value of its member, anything else only names it.
                if( jv.GetValueKind() == System.Text.Json.JsonValueKind.Number && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) )
                    member.value = text;

                // Members must differ; the names of `<=` and `>=` are the same once they are identifiers.
                member.name = recase(member.name, n => en.fields.Any(o => o != member && o.name.Equals(n, StringComparison.OrdinalIgnoreCase)));
                if( member.name != text ) member.source = text;
            }

            return en;
        }

        public static Pack get_or_new(string ref_path)
        {
            var p = root;
            foreach( var n in segments(ref_path) ) p = p.child(brush(n, ""));
            return p;
        }

        readonly Dictionary<string, Pack> children_by_key = new();

        /// <summary>Slash-separated keys from the root: the path <see cref="get_or_new"/> and <see cref="get(string)"/> take.</summary>
        public string path => parent == null || parent == root ?
                                  key :
                                  parent.path + "/" + key;

        public Pack? find_child(string key) => children_by_key.GetValueOrDefault(key);

        /// <summary>
        /// The child pack with this key, created when missing. A pack is always found by its key. Its name is
        /// the key for now; <see cref="settle_names"/> changes it when it cannot stay.
        /// </summary>
        public Pack child(string key)
        {
            if( children_by_key.TryGetValue(key, out var next) ) return next;

            children.Add(children_by_key[key] = next = new Pack { key = key, name = key, parent = this });
            return next;
        }

        /// <summary>False for an alias of another schema: it is followed, never written.</summary>
        public bool is_emitted => string.IsNullOrEmpty(Reference);

        /// <summary>
        /// A pack that says nothing itself and only holds others: a segment of a path of the document
        /// (`components`, `schemas`, the path of an operation). It keeps the packs apart while they are looked
        /// up by key, and it is not written: what it holds is declared where the scope would stand, so the
        /// description has no wrapper that mirrors the layout of the source file.
        /// </summary>
        public bool is_scope => this != root && is_emitted && !built && !is_enum && !declared_empty && 0 < children.Count &&
                                fields.Count == 0 && imports.Count == 0 && inherits.Count == 0 && blocked.Count == 0 &&
                                members.Count == 0 && HeaderFor.Count == 0 && projection_of == null && comment == "" && attributes == "";

        /// <summary>The packs declared in this one once the scopes are seen through, in the order they are written.</summary>
        public IEnumerable<Pack> visible_children => children.SelectMany(c => c.is_scope ? c.visible_children : [c]);

        /// <summary>
        /// What the pack is called when its name is taken by a pack of another kind (a response and a schema both
        /// named `NotFound`): the kind tells them apart better than a letter case or a number would.
        /// </summary>
        public string? fallback;

        /// <summary>A schema with nothing in it (`{}`): any JSON.</summary>
        public bool is_empty => !is_enum && fields.Count == 0 && imports.Count == 0 && inherits.Count == 0 && !is_header && projection_of == null;

        /// <summary>
        /// A pack whose members are all constants: nothing of it is transmitted but the fact that it is there.
        /// As a request or a reply that is AdHoc's empty pack, the cheapest signal there is; as the type of a
        /// field it is a `bool`.
        /// </summary>
        public bool only_constants => !is_enum && 0 < fields.Count && fields.All(f => f.constant != null) &&
                                      imports.Count == 0 && inherits.Count == 0 && !is_header && projection_of == null;

        /// <summary>
        /// Set for an object the document declares with no properties (`properties: {}`, or closed with
        /// `additionalProperties: false`). Unlike `{}` - any JSON - it is an empty pack by intent.
        /// </summary>
        public bool declared_empty;

        /// <summary>A pack that carries no value: only constants, or empty by declaration. As the type of a field it is a `bool`.</summary>
        public bool says_nothing => only_constants || declared_empty && is_empty;

        /// <summary>True when the pack has something to put on the wire of its own or of its parents.</summary>
        public bool carries_data => fields.Any(f => f.constant == null) || 0 < imports.Count || 0 < inherits.Count || 0 < blocked.Count;

        /// <summary>Set on a direction projection (`Pet_Write` / `Pet_Read`): the pack it projects and the direction.</summary>
        public (Pack src, bool write)? projection_of;

        public override void write(StringBuilder dst)
        {
            if( is_scope ) // not a declaration of its own: what it holds stands in its place
            {
                foreach( var pack in children.Where(p => p.is_emitted).ToList() ) pack.write(dst);
                return;
            }

            if( this != root )
            {
                dst.AppendLine()
                   .Append(_comment(comment))
                   .Append(raw_doc)
                   .Append(string.Join('\n', attributes))
                   .AppendLine();
                dst.Append($"public {(is_enum ? "enum" : "class")} {name}");

                if( inherits.Count == 1 )
                {
                    // When `get()` cannot resolve the path (e.g. the inherit names an external
                    // type like `Duration` from `org.unirail.Meta`, imported via `using`), fall
                    // back to the literal token rather than emitting an empty parent name.
                    var inheritPath = inherits.First();
                    dst.Append($" : {get(inheritPath)?.ToString() ?? inheritPath} {{\n");
                }
                else if( inherits.Count > 1 )
                {
                    // OpenAPI `allOf` with multiple refs → AdHoc multi-inheritance via `_<(A,B,C)>`.
                    var parents = string.Join(", ", inherits.Select(i => get(i)?.ToString() ?? i));
                    dst.Append($" : _<({parents})> {{\n");
                }
                else
                    dst.Append($"{(is_header ? $" : HeaderFor<{(HeaderFor.Count == 1 ? HeaderFor[0].ToString() : $"({string.Join(", ", HeaderFor)})")}>" : "")} {{\n");
            }

            // The scopes of an OAuth scheme may be one scope: an enum cannot, so a filler stands in.
            if( is_enum && fields.Count < 2 ) new Field(this, "one_more_field", (string)null!) { source = "filler: an AdHoc enum needs at least two members" };

            foreach( var member in members ) dst.Append(member).Append('\n');

            // Snapshot `fields` and `children` before iterating: Field.write → get_type_string
            // may trigger format_alias(), which lazily creates TYPEDEF alias Packs elsewhere
            // in the tree. Without snapshotting we'd collide with our own enumeration.
            foreach( var fld in fields.ToList() ) fld.write(dst);
            foreach( var pack in children.Where(p => string.IsNullOrEmpty(p.Reference)).ToList() ) pack.write(dst);

            if( this != root ) dst.Append("\n}\n");
        }
    }

    public static Pack root = new();

    public class Actor{
        // ── Param ─────────────────────────────────────────────────────────
        /**
         * Represents a typed slot in a request or response.
         *
         * typeName:  when set, the literal type token to emit (e.g. "StandardErrors").
         * pack:      when set, the Pack whose name/reference provides the type.
         *
         * GetTypeName() resolves whichever is relevant.
         */
        public class Param{
            public Pack?   pack;
            public string? typeName;
            public string  comment  = "";
            public string  httpCode = "";

            /// <summary>Construct from a pre-resolved type name string.</summary>
            public Param(string typeName) => this.typeName = typeName;

            /// <summary>Construct from a Pack (type name resolved at write time).</summary>
            public Param(Pack? pack) => this.pack = pack; // no pack: nothing to carry, NoArg

            /// <summary>The pack this param carries; an alias of another schema is followed to that schema.</summary>
            public Pack? target => pack == null                                                            ? null :
                                   !string.IsNullOrEmpty(pack.Reference) && get(pack.Reference) is Pack to ? to :
                                                                                                             pack;

            /// <summary>Returns the AdHoc type token for this param. Final only after settle_names().</summary>
            public string GetTypeName() => !string.IsNullOrEmpty(typeName) ?
                                               typeName! :
                                               target?.ToString() ?? "NoArg";
        }

        // ── Actor fields ───────────────────────────────────────────────────
        public Actor?       parent;
        public string       name = "";
        public Param?       request;
        public List<Param>  response   = [];
        public List<Actor>  children   = [];
        public string       comment    = "";
        public string       attributes = "";
        public List<string> tags       = []; // OpenAPI operation tags - propagated to the Dashboard

        public Dictionary<string, List<string>> links = new();
        public bool                             HasLinks => links.Count > 0;
        public bool                             forceExplicit    = false;
        public Dictionary<string, List<string>> pendingLinkOpIds = new();

        /// <summary>
        /// True for an HTTP operation; false for a segment of a path, which only holds what is declared under
        /// it, and for the root that stands for a connection.
        /// </summary>
        public bool IsOperation;

        /// <summary>
        /// What it is looked up by. Of a segment: the segment as the document writes it, `{petId}`. Of an
        /// operation: a name no other operation of the document has - its packs and its states are named after
        /// it. The name it is declared under is another thing, and is final only after settle_names().
        /// </summary>
        public string key = "";

        // Where HTTP finds it: of an operation the path and the method - what an `operationRef` of a link points
        // at; of a segment the path up to it.
        public string http_method  = "";
        public string operation_id = ""; // as the document writes it; the name may have had to change
        public string route       = "";

        /// <summary>The keys of the operations made so far: one name is one operation.</summary>
        public static readonly HashSet<string> operation_keys = new();

        /// <summary>
        /// The interface an operation of this path is declared in: one per segment, nested as the path goes.
        /// `/pets/{petId}` is `pets` and, in it, `petId`; the root path is the connection itself. A webhook has
        /// no path: its name is its one segment.
        /// </summary>
        public static Actor container(string path, Actor connection)
        {
            var a     = connection;
            var route = "";
            foreach( var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries) )
            {
                route += (connection == root_actor ? "/" : route == "" ? "" : "/") + segment;

                var next = a.children.FirstOrDefault(c => !c.IsOperation && c.key == segment);
                if( next == null )
                    a.children.Add(next = new Actor
                                          {
                                              key    = segment,
                                              name   = brush(segment.Replace("{", "").Replace("}", ""), ""),
                                              parent = a,
                                              route  = route
                                          });
                a = next;
            }

            return a;
        }

        /// <summary>
        /// The actor of one HTTP operation, declared where its path puts it. <paramref name="key"/> becomes its own:
        /// an `operationId` the document repeats, or two derived names that coincide, are told apart by a number.
        /// </summary>
        public static Actor operation(Actor where, string name, string key)
        {
            key = brush(key, "");
            var unique = key;
            for( var i = 2; !operation_keys.Add(unique); i++ ) unique = key + i;

            var actor = new Actor { key = unique, name = brush(name, ""), parent = where, IsOperation = true };
            where.children.Add(actor);
            return actor;
        }

        public Actor add_comment(string? comment)
        {
            if( !string.IsNullOrEmpty(comment) ) this.comment += comment + "\n";
            return this;
        }

        /// <summary>The path of the declaration inside its connection: `pets.petId.showPetById`.</summary>
        public override string ToString() => parent == null || parent == root_actor || parent == root_webhook_actor ?
                                                 name :
                                                 parent + "." + name;

        // ── write ──────────────────────────────────────────────────────────
        public void write(StringBuilder dst)
        {
            // Root: the connection, what is declared in it one after another
            if( this == root_actor || this == root_webhook_actor )
            {
                foreach( var child in children ) child.write(dst);
                return;
            }

            // An operation whose name had to change still says what the document calls it.
            if( IsOperation && operation_id != "" && operation_id != name ) add_comment($"operationId: {operation_id}");

            if( !IsOperation ) WriteAsContainer(dst);
            else if( HasCallbacks || HasLinks || HasStream || forceExplicit ) WriteAsExplicitActor(dst);
            else WriteAsShorthand(dst);
        }

        /// <summary>
        /// A segment of a path: an interface that holds the operations of the path and the segments below it.
        /// The name says the segment; where it cannot - a parameter `{petId}`, a segment that is not an
        /// identifier - a comment above it gives the path as the document writes it.
        /// </summary>
        void WriteAsContainer(StringBuilder dst)
        {
            dst.AppendLine();
            if( name != key ) dst.AppendLine($"// {route}");
            dst.Append(_comment(comment));
            dst.AppendLine($"public interface {name} {{");
            foreach( var child in children ) child.write(dst);
            dst.AppendLine("}").AppendLine();
        }

        // A link target's states are GRAFTED (copied) into the referencing actor's FSM, and state
        // names must be unique within one actor - so every state name carries the key of its actor
        // (`Call_getPet`, not `Call`), letting any actor graft any other without collisions.
        public static string CallState(string key) => $"Call_{key}";

        void WriteAsExplicitActor(StringBuilder dst)
        {
            var name = key; // the states are named by the key: it is the actor's own in the whole document

            dst.Append(_comment(comment, "        "));
            if( !string.IsNullOrEmpty(attributes) ) dst.AppendLine($"        {attributes.Trim()}");
            dst.AppendLine($"        public interface {this.name} : Actor {{");
            dst.AppendLine($"            int MaxActiveInstances => UNLIMITED;");

            // State 1: Call - transitional L branch to Return
            var reqType = request?.GetTypeName() ?? "NoArg";
            dst.AppendLine().AppendLine($"            [L____________<Return_{name}, {reqType}>]");
            dst.AppendLine($"            struct {CallState(name)} {{ }}").AppendLine();

            // State 2: Return - one attribute per distinct target (packs grouped per target)
            var returnGroups = new Dictionary<string, List<string>>();
            foreach( var resp in response )
            {
                var code = resp.httpCode;

                string target;
                if( links.TryGetValue(code, out var codeLinks) && codeLinks.Count > 0 )
                    target = codeLinks.Count == 1 && !HasCallbacks ?
                                 codeLinks[0] : // the call state of the linked operation
                                 $"Links_{code}_{name}";
                else
                    target = HasCallbacks ?
                                 $"CallbackState_{name}" :
                                 "End";

                if( !returnGroups.TryGetValue(target, out var list) )
                    returnGroups[target] = list = new List<string>();
                list.Add(resp.GetTypeName());
            }

            var end = HasCallbacks ?
                          $"CallbackState_{name}" :
                          "End";

            // A streamed answer: the items come while the actor stays in this state, and a transitional reply
            // ends it - one of the complete replies, or an empty pack when the stream simply runs out. A pack
            // that is also a complete reply cannot be an item: nothing on the wire would tell the two apart.
            var items = stream.Where(s => response.All(r => r.target != s.target)).ToList();
            if( 0 < items.Count )
            {
                if( !returnGroups.TryGetValue(end, out var closing) ) returnGroups[end] = closing = [];
                if( !closing.Contains("NoArg") ) closing.Add("NoArg");
                dst.AppendLine($"            [____________r<{ToTuple(items)}>]");
            }

            // The spec describes no response: the call still has to return, with nothing to carry.
            if( returnGroups.Count == 0 ) returnGroups[end] = ["NoArg"];

            foreach( var (target, packs) in returnGroups )
            {
                var packsExpr = packs.Count == 1 ?
                                    packs[0] :
                                    "(" + string.Join(", ", packs) + ")";
                dst.AppendLine($"            [____________R<{target}, {packsExpr}>]");
            }

            dst.AppendLine($"            struct Return_{name} {{ }}").AppendLine();

            // State 3: Link-choice states - only emitted when a code has MULTIPLE link targets,
            // or when a single link coexists with a callback state.
            foreach( var (code, targetActors) in links )
            {
                if( targetActors.Count == 1 && !HasCallbacks ) continue; // already inlined in Return above

                foreach( var target in targetActors )
                    dst.AppendLine($"            [L____________<{target}, NoArg>]");
                dst.AppendLine($"            [L____________<{(HasCallbacks ? $"CallbackState_{name}" : "End")}, NoArg>]");
                dst.AppendLine($"            struct Links_{code}_{name} {{ }}").AppendLine();
            }

            // State 4: Persistent Callback State (Followers + graceful close)
            if( HasCallbacks )
            {
                if( callbackLPacks.Count > 0 )
                    dst.AppendLine($"            [l____________<{ToTuple(callbackLPacks)}>]");
                if( callbackRPacks.Count > 0 )
                    dst.AppendLine($"            [____________r<{ToTuple(callbackRPacks)}>]");
                dst.AppendLine($"            [L____________<End, NoArg>]"); // Allow client to close session
                dst.AppendLine($"            struct CallbackState_{name} {{ }}");
            }

            dst.AppendLine("        }");
            dst.AppendLine();
        }

        private void WriteAsShorthand(StringBuilder dst)
        {
            // 1. Doc-comment for the operation
            if( !string.IsNullOrEmpty(comment) ) { dst.Append(_comment(comment, "            ")); }

            // Attributes (Tags, SecurityRequirement, etc.)
            if( !string.IsNullOrEmpty(attributes) )
                dst.AppendLine($"            {attributes.Trim()}");

            // 2. Return tuple (Responses)
            var retSb = new StringBuilder();
            retSb.Append("(L____________");
            foreach( var rp in response )
            {
                retSb.AppendLine(",");
                retSb.Append(_comment(rp.comment)); // internal parameter comments also use ///
                retSb.Append(rp.GetTypeName());
            }

            // The spec describes no response: the call still has to return, with nothing to carry.
            if( response.Count == 0 ) retSb.Append(", NoArg");

            retSb.Append(')');

            // ... rest of method (request params) ...
            var reqStr = request != null ?
                             $"{request.GetTypeName()} req" :
                             "NoArg _";
            dst.AppendLine($"            {retSb} {name}({reqStr});");
            dst.AppendLine();
        }

        public List<Param> callbackLPacks = new(); // Packs the Client (Left) can send in Callback state
        public List<Param> callbackRPacks = new(); // Packs the Server (Right) can send in Callback state

        public bool HasCallbacks => callbackLPacks.Count > 0 || callbackRPacks.Count > 0;

        // The items of a streamed answer (server-sent events, JSON lines): sent while the actor stays in its reply state.
        public List<Param> stream = new();
        public bool HasStream => stream.Count > 0;

        // Helper to format a list of Params into an AdHoc type string
        private string ToTuple(List<Param> list)
        {
            switch( list.Count )
            {
                case 0:  return "NoArg";
                case 1:  return list[0].GetTypeName();
                default: return "(" + string.Join(", ", list.Select(p => p.GetTypeName())) + ")";
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Statics
    // ─────────────────────────────────────────────────────────────────────────
    public static Actor         root_actor         = new() { name = "", parent = null };
    public static Actor         root_webhook_actor = new() { name = "", parent = null };
    public static StringBuilder hosts      = new();

    public static void read_servers(IList<OpenApiServer>? Servers)
    {
        // OpenAPI `servers` are alternative base URLs of the SAME API (prod/staging/regions) -
        // they are ONE AdHoc host carrying one [Server(...)] attribute per URL, not N hosts
        // (an extra host would need its own Connection and duplicate the whole protocol).

        // 1. SUMMARY
        hosts.AppendLine("\n    /// <summary>");
        hosts.AppendLine("    /// The API server. Server Definition: https://swagger.io/specification/#server-object");
        if( Servers != null )
            foreach( var server in Servers )
            {
                hosts.AppendLine($"    /// {xml_escape(server.Url ?? "")}");
                if( !string.IsNullOrEmpty(server.Description) )
                    foreach( var line in server.Description.Split('\n') )
                        hosts.AppendLine($"    ///     {xml_escape(line.Trim())}");

                if( server.Variables?.Count > 0 )
                    foreach( var (varName, variable) in server.Variables )
                    {
                        hosts.AppendLine($"    ///     Variable '{xml_escape(varName)}': https://swagger.io/specification/#server-variable-object");
                        if( !string.IsNullOrEmpty(variable.Description) )
                            foreach( var line in variable.Description.Split('\n') )
                                hosts.AppendLine($"    ///     {xml_escape(line.Trim())}");
                    }
            }

        hosts.AppendLine("    /// </summary>");
        hosts.AppendLine("    ///<see cref = 'InTS'/>   implementation in TypeScript");
        hosts.AppendLine("    ///<see cref = 'InCS'/>   implementation in C#");
        hosts.AppendLine("    ///<see cref = 'InJAVA'/> implementation in JAVA");
        hosts.AppendLine("    ///<see cref = 'InCPP'/>  implementation in C++");
        hosts.AppendLine("    ///<see cref = 'InRS'/>   implementation in RUST");
        hosts.AppendLine("    ///<see cref = 'InGO'/>   implementation in GO");

        // 2. ATTRIBUTES - one [Server] per URL, [ServerVariable] keyed by its server's URL
        if( Servers != null )
            foreach( var server in Servers )
            {
                hosts.Append(add_attribute("Server", ["Url", "Description", "Name"],
                                           [$"\"{server.Url}\"", $"\"{server.Description}\"", $"\"{server.Name}\""], true));

                if( server.Extensions?.Count > 0 )
                    foreach( var (ek, ev) in server.Extensions )
                        hosts.Append(add_attribute("Extension", ["Key", "Value"], [$"\"{ek}\"", $"\"{ext_value(ev)}\""], true));

                if( server.Variables?.Count > 0 )
                    foreach( var (varName, variable) in server.Variables )
                    {
                        var enumList = (variable.Enum?.Count > 0) ?
                                           string.Join("|", variable.Enum) :
                                           "";

                        hosts.Append(add_attribute("ServerVariable",
                                                   ["ServerUrl", "Name", "Default", "Description", "Enum"],
                                                   [
                                                       $"\"{server.Url}\"", $"\"{varName}\"", $"\"{variable.Default}\"",
                                                       $"\"{variable.Description}\"", $"\"{enumList}\""
                                                   ], true));

                        if( variable.Extensions?.Count > 0 )
                            foreach( var (vk, vv) in variable.Extensions )
                                hosts.Append(add_attribute("Extension", ["Key", "Value"], [$"\"{vk}\"", $"\"{ext_value(vv)}\""], true));
                    }
            }

        // 3. STRUCT DEFINITION - exactly one host
        hosts.AppendLine("    struct Server : Host { }");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Reference / name utilities
    // ─────────────────────────────────────────────────────────────────────────
    static string clean_path(string refPath) => refPath
                                                .Replace("{", "I__").Replace("}", "__I")
                                                .Replace(":", "").Replace('-', '_')
                                                .Replace("[", "").Replace("]", "").Replace(" ", "_")
                                                .TrimStart('#', '/').TrimEnd('/');

    static StringBuilder tmp = new();

    public static object? get(string ref_path) => get(ref_path, new HashSet<string>());

    private static object? get(string ref_path, HashSet<string> visited)
    {
        if( !visited.Add(ref_path) )
        {
            var p_nr = root;
            foreach( var n in segments(ref_path) )
            {
                p_nr = p_nr.find_child(brush(n, ""))!;
                if( p_nr == null ) return null;
            }

            return p_nr;
        }

        var p    = root;
        var path = segments(ref_path);

        for( var i = 0; i < path.Length; i++ )
        {
            var name = brush(path[i], "");

            if( i == path.Length - 1 )
            {
                var fld = p.fields.FirstOrDefault(f => f.key == name);
                if( fld != null ) return fld;

                var foundPack = p.find_child(name);
                if( foundPack == null ) return null;
                if( !string.IsNullOrEmpty(foundPack.Reference) )
                    return get(foundPack.Reference, visited);
                return foundPack;
            }

            var next = p.find_child(name);
            if( next == null ) return null;
            p = next;
        }

        return p;
    }

    /// <summary>The segments of a reference path (`#/components/schemas/Pet`); the root path `/` has none.</summary>
    static string[] segments(string ref_path) => clean_path(ref_path).Split('/', StringSplitOptions.RemoveEmptyEntries);

    public static string brush(string name, string class_name)
    {
        name = name.Trim()
                   .Replace('.', 'ˍ')
                   .Replace("[", "").Replace("]", "");

        // Any remaining character that is not a valid C# identifier character → '_'
        // (real-world specs use ':', '@', '$', '+', spaces etc. in names - e.g. OAuth scopes "read:pets").
        var chars = new StringBuilder(name.Length);
        foreach( var c in name ) chars.Append(char.IsLetterOrDigit(c) || c == '_' || c == 'ˍ' ? c : '_');
        name = chars.ToString();

        // AdHoc rejects entity names that start or end with an underscore (`_links`, `$ref`, `@type` …).
        name = name.Trim('_');
        if( name.Length == 0 ) name = "unnamed";
        if( char.IsDigit(name[0]) ) name = "N" + name;

        return AdHocNames.brush(name, class_name);
    }

    /// <summary>The version of the specification the document was written in, for the header of the file.</summary>
    static string spec_version(OpenApiSpecVersion? v) => v switch
                                                         {
                                                             null                           => "OpenAPI",
                                                             OpenApiSpecVersion.OpenApi2_0  => "Swagger 2.0",
                                                             _                              => "OpenAPI " + v.ToString()!.Replace("OpenApi", "").Replace('_', '.')
                                                         };

    static string banner(string title) => $"\n// ═════════════════════════ {title} ═════════════════════════\n\n";

    // ─────────────────────────────────────────────────────────────────────────
    //  Layout of the emitted file
    //
    //  The model writes its pieces flat; this pass indents them by nesting depth and squeezes the
    //  blank lines. It reads the text the way the C# lexer does, so a brace inside a string or a
    //  comment does not count, and the lines a multi-line verbatim string spans stay exactly as
    //  they are: they are data.
    // ─────────────────────────────────────────────────────────────────────────
    static string tidy(string src)
    {
        var dst      = new StringBuilder(src.Length + src.Length / 3);
        var depth    = 0;     // { } nesting
        var parens   = 0;     // ( ) nesting: the continuation lines of a return tuple
        var comment  = false; // inside /* */
        var verbatim = false; // inside @"…"
        var blank    = false; // a blank line is pending
        var prev     = "";    // the previous emitted line, trimmed

        foreach( var raw in src.Replace("\r", "").Split('\n') )
        {
            var data = verbatim; // the line continues a verbatim string
            var line = data ?
                           raw :
                           raw.Trim();

            if( !data )
            {
                if( line.Length == 0 )
                {
                    blank = true;
                    continue;
                }

                // An empty body closes on the line that opened it: `class NoArg { }`.
                if( line == "}" && prev.EndsWith('{') && !prev.StartsWith("//") && !comment )
                {
                    dst.Length--; // the line break
                    dst.Append(" }\n");
                    depth--;
                    blank = false;
                    prev  = "}";
                    continue;
                }

                // One blank line at most, and none where it only loosens the text: after an opening brace,
                // before a closing one, between a doc comment or an attribute and what it belongs to.
                if( blank && prev != "" && !prev.EndsWith('{') && line[0] != '}' && parens == 0 &&
                    (comment || !(prev.StartsWith("///") || prev.EndsWith("*/") || prev.EndsWith(")]") || prev[0] == '[' && prev[^1] == ']')) )
                    dst.Append('\n');

                var level = comment          ? line.StartsWith("*/") ? depth : depth + 1 :
                            line[0] == '}'   ? depth - 1 :
                            0 < parens       ? depth + 1 :
                                               depth;
                dst.Append(' ', 4 * Math.Max(level, 0));
            }

            blank = false;
            dst.Append(line).Append('\n');
            prev = line.Trim();

            for( var i = 0; i < line.Length; i++ )
            {
                var c = line[i];
                if( verbatim )
                {
                    if( c != '"' ) continue;
                    if( i + 1 < line.Length && line[i + 1] == '"' ) i++; // "" is an escaped quote
                    else verbatim = false;
                    continue;
                }

                if( comment )
                {
                    if( c == '*' && i + 1 < line.Length && line[i + 1] == '/' )
                    {
                        comment = false;
                        i++;
                    }

                    continue;
                }

                var next = i + 1 < line.Length ?
                               line[i + 1] :
                               '\0';
                switch( c )
                {
                    case '/' when next == '/':
                        i = line.Length; // the rest of the line is a comment
                        break;
                    case '/' when next == '*':
                        comment = true;
                        i++;
                        break;
                    case '@' when next == '"':
                        verbatim = true;
                        i++;
                        break;
                    case '"': // a regular string ends on its line
                        for( i++; i < line.Length && line[i] != '"'; i++ )
                            if( line[i] == '\\' )
                                i++;
                        break;
                    case '{': depth++; break;
                    case '}': depth--; break;
                    case '(': parens++; break;
                    case ')': parens--; break;
                }
            }
        }

        return dst.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>Text squeezed into one line, for a `//` comment.</summary>
    static string one_line(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string GetReferencePath(BaseOpenApiReference reference)
    {
        string typeSegment;
        switch( reference.Type )
        {
            case ReferenceType.RequestBody:    typeSegment = "requestBodies"; break;
            case ReferenceType.SecurityScheme: typeSegment = "securitySchemes"; break;
            default:                           typeSegment = reference.Type.ToString().ToLowerInvariant() + "s"; break;
        }

        return $"#/components/{typeSegment}/{reference.Id}";
    }
}
