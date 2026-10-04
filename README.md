# OpenAPI-to-AdHoc - OpenAPI / Swagger → AdHoc protocol description

> One of the [**converters to AdHoc protocol**](https://github.com/AdHoc-Protocol#converters-to-adhoc-protocol).
> Take a protocol you already have, get an [AdHoc](https://github.com/AdHoc-Protocol/AdHoc-protocol) description,
> open it in the Observer. The result is a starting point you refine by hand, not a finished protocol.

Converts [OpenAPI](https://www.openapis.org/) 3.2 / 3.1 / 3.0 and Swagger 2.0 documents (`.json`, `.yaml`, `.yml`)
into [AdHoc](https://github.com/AdHoc-Protocol) protocol-description `.cs` files, ready for AdHocAgent to generate
Java / C# / C++ / TypeScript / Go / Rust code from.

An OpenAPI document describes a REST API: resources behind URLs, verbs, media types, status codes. AdHoc is a
binary protocol over a connection, and it says more than OpenAPI can. So the conversion is by essence, not by
syntax: an operation becomes a call with a typed request and typed replies, a schema becomes a pack, a named
number becomes an alias that carries its range, a value that never varies becomes a constant, a stream of
server-sent events becomes a state of an actor, and what only exists because of HTTP - the URL, the verb, media
types, where a parameter travels - is kept as metadata or dropped.

This is the C# member of the family: the converter is a .NET console program built on the
[Microsoft.OpenApi](https://github.com/microsoft/OpenAPI.NET) reader. The project is fully isolated: it carries
its own copy of the naming rules (`src/adhoc/AdHocNames.cs`) and its own `validate.sh`, and references nothing
outside its own folder.

## Links

- OpenAPI Specification: https://spec.openapis.org/oas/latest.html
  ([3.2.0](https://spec.openapis.org/oas/v3.2.0.html), [3.1.0](https://spec.openapis.org/oas/v3.1.0.html),
  [3.0.3](https://spec.openapis.org/oas/v3.0.3.html), [Swagger 2.0](https://swagger.io/specification/v2/));
  its sources: https://github.com/OAI/OpenAPI-Specification
- The registry of formats (`int8` … `uint64`, `date`, `uuid`, …): https://spec.openapis.org/registry/format/
- OpenAPI data types: https://swagger.io/docs/specification/v3_0/data-models/data-types/
- The reader this converter is built on: https://github.com/microsoft/OpenAPI.NET
  (NuGet [Microsoft.OpenApi](https://www.nuget.org/packages/Microsoft.OpenApi) and
  [Microsoft.OpenApi.YamlReader](https://www.nuget.org/packages/Microsoft.OpenApi.YamlReader), restored by the build)

Sample documents fetched by `fetch-samples.sh` (all verified to download). Each name leads to the original;
[`samples/sources.txt`](samples/sources.txt) lists the same pages, and the header of every description links
its original:

| File | Source |
|:--|:--|
| [`petstore.yaml`](https://github.com/OAI/learn.openapis.org/blob/main/examples/v3.0/petstore.yaml), [`petstore-expanded.yaml`](https://github.com/OAI/learn.openapis.org/blob/main/examples/v3.0/petstore-expanded.yaml), [`uspto.yaml`](https://github.com/OAI/learn.openapis.org/blob/main/examples/v3.0/uspto.yaml), [`link-example.yaml`](https://github.com/OAI/learn.openapis.org/blob/main/examples/v3.0/link-example.yaml), [`callback-example.yaml`](https://github.com/OAI/learn.openapis.org/blob/main/examples/v3.0/callback-example.yaml), [`api-with-examples.yaml`](https://github.com/OAI/learn.openapis.org/blob/main/examples/v3.0/api-with-examples.yaml) | OpenAPI Initiative examples, 3.0: https://github.com/OAI/learn.openapis.org/tree/main/examples |
| [`webhook-example.yaml`](https://github.com/OAI/learn.openapis.org/blob/main/examples/v3.1/webhook-example.yaml), [`non-oauth-scopes.yaml`](https://github.com/OAI/learn.openapis.org/blob/main/examples/v3.1/non-oauth-scopes.yaml), [`tictactoe.yaml`](https://github.com/OAI/learn.openapis.org/blob/main/examples/v3.1/tictactoe.yaml) | OpenAPI Initiative examples, 3.1: same repository |
| [`swagger-petstore-v3.yaml`](https://github.com/swagger-api/swagger-petstore/blob/master/src/main/resources/openapi.yaml) | Swagger Petstore 3.0: https://github.com/swagger-api/swagger-petstore |
| [`swagger-petstore-v2.json`](https://petstore.swagger.io/v2/swagger.json) | Swagger 2.0 petstore: https://petstore.swagger.io/ |
| [`grafana.json`](https://github.com/grafana/grafana/blob/main/public/openapi3.json) | Grafana HTTP API: https://github.com/grafana/grafana |
| [`docker-engine.yaml`](https://github.com/moby/moby/blob/master/api/swagger.yaml) | Docker Engine API (Swagger 2.0): https://github.com/moby/moby |

## Before and after

`petstore.yaml` (119 lines) and the `AdHoc/petstore.cs` it generates (172 lines), both abridged here. Full files:
[the original](https://github.com/OAI/learn.openapis.org/blob/main/examples/v3.0/petstore.yaml) and [the result](AdHoc/petstore.cs).

**OpenAPI**

```yaml
paths:
  /pets:
    get:
      operationId: listPets
      parameters:
        - name: limit
          in: query
          description: How many items to return at one time (max 100)
          schema: {type: integer, maximum: 100, format: int32}
      responses:
        '200':
          description: A paged array of pets
          content:
            application/json:
              schema: {$ref: '#/components/schemas/Pets'}
        default:
          description: unexpected error
          content:
            application/json:
              schema: {$ref: '#/components/schemas/Error'}
components:
  schemas:
    Pet:
      type: object
      required: [id, name]
      properties:
        id:   {type: integer, format: int64}
        name: {type: string}
        tag:  {type: string}
    Pets:
      type: array
      maxItems: 100
      items: {$ref: '#/components/schemas/Pet'}
```

**AdHoc protocol description**

```csharp
/// <summary>Shared error pack set - error schemas reused across operations.</summary>
public interface StandardErrors : _<Error> { }

public class Pet {
    long id;
    string name;
    string? tag;                                  // not in `required`: optional
}

public class Pets {                               // a named list: an alias that carries its bound
    [D(100)]
    Pet[,,] TYPEDEF;
}

public class listPetsReq {                        // the parameters of the operation
    /// <summary>How many items to return at one time (max 100)</summary>
    [V(100)]                                      // a ceiling and no floor: varint from the top
    int? limit; // in: query
}

/// <summary>A paged array of pets</summary>
public class listPets_200 {                       // a list is not a pack: a pack of the operation carries it
    Pets value;
}

interface ClientServerConnection : Connects<Client, Server> {
    public interface pets {                       // the path /pets
        [HttpMethod(@"GET")]
        (L____________,
            /// <summary>A paged array of pets</summary>
            listPets_200,
            /// <summary>shared errors</summary>
            StandardErrors) listPets(listPetsReq req);

        // /pets/{petId}
        public interface petId {
            [HttpMethod(@"GET")]
            (L____________, Pet, StandardErrors) showPetById(showPetByIdReq req);
        }
    }
}
```

The operation became an RPC method: the Client (Left) calls, the Server answers with the page or with an error.
The named list became an alias that keeps its bound, and the `default` error - used by every operation of this
API - became a named pack set.

The path of an operation is not quoted in an attribute: it is where the operation is declared. `/pets/{petId}`
is the interface `petId` in the interface `pets`, so the generated API is laid out the way the URLs are; only the
HTTP method is left to an attribute, and a gateway for legacy HTTP clients can still be built on top. The words
of the format itself are another matter: there is no `components { schemas { ... } }` around the packs and no
`paths { ... }` around the operations, because they say nothing about the API.

## Layout

| Path | Contents |
|:--|:--|
| `src/OpenAPI2AdHoc.cs` | The converter: the model (packs, fields, operations), the name pass, the emitter |
| `src/Originals.cs` | Reads `sources.txt`: the original page of an input file, for the header of its description |
| `src/adhoc/AdHocNames.cs` | Local copy of AdHocAgent's keyword list and its `brush` rename rule |
| `OpenAPI2AdHoc.csproj` | .NET 10 console project; compiles `src/` only |
| `fetch-samples.sh`, `samples/` | 13 real documents, the script that downloads them and `samples/sources.txt`, the original page of each |
| `build.sh` | Builds the converter into `out/` and regenerates `AdHoc/` from `samples/` |
| `AdHoc/*.cs` | Generated descriptions, one per document, kept for review |
| `validate.sh` | AdHocAgent parse-only check of a folder of descriptions |
| `tests/` | Four synthetic documents - every feature once, every edge case, what 3.2 added - with their descriptions |
| `CONVENTIONS.md` | The rules every converter of the family follows |

## What gets generated

For each document `<name>.yaml` the converter writes `<name>.cs` (`namespace org.openapi`, `interface <name>`;
characters that cannot be in an identifier become `_`). Its header names the document, with the page of its
original when a `sources.txt` in a folder above lists it (`fetch-samples.sh` writes one for the samples). Then, in
order:

1. **Packs Inventory (Dashboard)** - one `<see cref='…'/>` line per pack that carries data, alphabetised and
   pre-tagged with the OpenAPI tags of the operations that use the pack, so branches can route by tag
   (`KeepDoc`). No `id` is pinned: pack ids are AdHoc's own and AdHocAgent assigns them.
2. **Project attributes** - `info`, `externalDocs`, the tag definitions and the `x-…` extensions of the document.
3. **`_DefaultMaxLengthOf`** with permissive caps (65 535), **`NoArg`** (the empty request / reply) and, when
   the same error replies recur in three or more operations, the **`StandardErrors`** pack set. A file with
   64-bit integers opens with a note on `longJS` and on varint: the two calls only the developer can make.
4. **Packs**, all declared side by side in the project - what `components` holds (security schemes, schemas,
   responses, the `parameters` and `headers` libraries, request bodies, format aliases), then what the
   converter makes for each operation, in the order of the document: `<operation>Req`, `<operation>_<code>`,
   the status sentinels `Code_<code>`, the header packs `<Header>_<type>`.
5. **Hosts** - `Server` (carrying one `[Server]` attribute per entry of `servers`), `Client`, and `Subscriber`
   when the document has webhooks; every host requests all six language generators.
6. **Connections** - `ClientServerConnection : Connects<Client, Server>` with the operations, each declared in the
   interfaces of its path, and `ServerSubscriberConnection : Connects<Server, Subscriber>` with the webhooks.
7. **Attribute declarations** - the custom attribute classes used above. AdHoc turns them into constants of the
   generated code; it does not read them.

## How OpenAPI maps to AdHoc

### Operations

HTTP tells the replies of a call apart by the status code; AdHoc tells them apart by the pack. So a status
becomes the name of a pack, never a number on the wire.

| OpenAPI | AdHoc | Notes |
|:--|:--|:--|
| operation | RPC shorthand `(L____________, Reply1, Reply2) name(Request req);` | Left = Client calls, Right = Server answers |
| path `/pets/{petId}` | nested interfaces, one per segment: `interface pets { interface petId { … } }` | the hierarchy of the API is declared, not quoted in an attribute; a segment that is a parameter, or is not an identifier, has its path in a comment above; `summary` / `description` of the path item are the doc of the innermost interface |
| `operationId` | the name of the method, declared where its path puts it | without one the method is called `get`, `post` …, and its packs are named by the method and the path, `get_pets_petId_200` |
| the HTTP method | `[HttpMethod(@"GET")]` | the one part of the route a declaration cannot say |
| parameters + request body | one `<operation>Req` pack: the body and the parameters, flattened | a body that is a bare `$ref` to an object, with no parameters, is used directly |
| parameter `in:` path / query / header / cookie | a note on the field: `// in: query` | not structure in AdHoc, but enough to build an HTTP gateway from the description |
| `$ref` to `components.parameters` | the field is **imported**: `/// <see cref="parameters.limit"/>+` | the pack `parameters` declares every reusable parameter once, the single source of truth; retype it there and every request follows |
| a parameter named like a field of the body | two fields, the parameter renamed | `PUT /users/{name}` with a body that has `name` carries two values |
| no parameters, no body / no described response | `NoArg` | one shared empty pack |
| response, `$ref` to `components.responses` | the pack of that response: `NotFound` | a named reply stays a reply of its own, whatever schema it carries |
| response with a schema `$ref` to an object | that pack, directly | |
| response with any other content | `<operation>_<code>`: `listPets_200` | an inline object gives its fields; a list, a map, a number, an alias is the `value` field |
| response with no content (`204`, `default`) | project-wide sentinel `Code_<code>` | an empty pack is AdHoc's cheapest signal: the reply itself is the information |
| replies used by ≥ 3 operations as errors | `interface StandardErrors : _<(…)>` | a named pack set, referenced instead of repeating the list |
| a success with a sequential media type - `text/event-stream`, JSON lines, `itemSchema` (3.2) | `interface <operation> : Actor`: the items go by a non-transitional branch while the actor stays in its reply state, an empty pack closes the stream | a complete answer in another media type stays a transitional reply next to it |
| `callbacks` | `interface <operation> : Actor` with a persistent callback state | the server pushes on the connection that exists; the URL expression is kept in the doc |
| response `links` | `interface <operation> : Actor`, the reply state transits to the call state of the linked operation | what the link passes (`id = $response.body#/id`) is kept in the doc |
| `webhooks` (3.1) | the same call, turned around, in `ServerSubscriberConnection` | the Server calls, the Subscriber answers; a webhook with several operations is an interface that holds them |
| response headers, numeric / boolean | `class <Header>_<type> : HeaderFor<(…)>` | one pack per header, listing every response that carries it; only for packs the operation owns |
| response headers, strings; headers on shared packs | a line in the doc of the operation | AdHoc headers are fixed-size primitives |
| `servers` (document, path, operation) | `[Server(url, description, name)]`, `[ServerVariable(…)]` | alternative base URLs are one host, not several |
| security schemes | a pack named like the scheme, with `[ApiKeyAuth]`, `[BasicAuth]`, `[BearerAuth]`, `[HttpAuth]`, `[OAuth2Auth…]`, `[OpenIdConnect]`, `[MutualTLS]`; OAuth scopes as an enum | metadata: authentication is the transport's job in AdHoc |
| `security` | `[SecurityRequirement(scheme, scopes)]` on the operation, `[GlobalSecurity]` on the pack `global`; `security: []` → `security: none` in the doc | |
| `tags` | `[Tag]` on the operation, and the tags on its packs in the Dashboard | |
| `deprecated` | `[Obsolete(message)]` | |
| `x-…` extensions | `[Extension(key, value)]` | objects and arrays as compact JSON |
| binary body of a request or a response; a media type with no schema | `File` conduit with `[S(…)]` and `[ContentType(media type)]` | streamed, not buffered; the cap is marked `TODO` |
| several media types with different schemas | one is carried (JSON first), the doc names the others | |

### Schemas

What a schema turns into is decided by its shape, and every reference to it follows.

| OpenAPI schema | AdHoc | Notes |
|:--|:--|:--|
| object with `properties` | `class` | a property outside `required`, or nullable, is optional `T?` |
| object declared with no properties (`properties: {}`, `additionalProperties: false`) | an empty `class` | a signal; a field typed with it is a `bool` |
| `allOf` | a `$ref` to an object is inheritance (`class Pet : NewPet`, several: `: _<(A, B)>`), an inline part adds its fields | in a schema and in a property alike |
| `oneOf` / `anyOf` | a pack whose alternatives are all optional fields `OneOf_<Schema>` | a tagged union, in a schema and in a property alike; an empty alternative costs one bit |
| `allOf: [$ref]`, `oneOf: [X, {type: null}]` | the type of `X` | the 3.0 and 3.1 ways to decorate or null a reference |
| `discriminator` | `[DiscriminatorProperty]` on the schema, `[DiscriminatorValue]` on every `mapping` target | on an `allOf` base the targets also inherit it; on a `oneOf` union they stay plain packs |
| `properties` + `additionalProperties` | the fields, and `Map<string, T> additionalProperties` for the rest | `patternProperties` likewise |
| `additionalProperties` alone | `Map<string, T>` | |
| inline object in a property | a nested pack named after the property | |
| `enum` of strings or whole numbers | `enum`; a named one is found by its `$ref`, an inline one that repeats a known list reuses it | `x-enum-varnames` / `x-enumNames` name the members, `x-enum-descriptions` document them |
| `const`, `enum` of one value | `public const` in the pack | a value that never varies is declared, not transmitted |
| named number, string, list or map | TYPEDEF alias: `class Limit { [MinMax(1, 100)] int TYPEDEF; }` | the name survives, and the alias carries the constraints to every field that uses it |
| `readOnly` / `writeOnly` property | `[ReadOnly]` / `[WriteOnly]`; the pack gets a `<Name>_Write` / `<Name>_Read` projection that excludes the field in the wrong direction | SSOT: the projection inherits the pack and lists the excluded fields |
| `{}`, bare `type: object`, several types at once, unresolved `$ref` | `Binary[,,]` with the note `// any JSON in the spec` | the only thing a binary protocol can do with "anything" |

### Types

| OpenAPI | AdHoc / C# | Notes |
|:--|:--|:--|
| `integer` (`int32`, none) | `int` | as every OpenAPI generator reads it |
| `integer` `int64` / `int16` / `int8` | `long` / `short` / `sbyte` | |
| `integer` `uint64` / `uint32` / `uint16` / `uint8` | `ulong` / `uint` / `ushort` / `byte` | |
| `number` (`double`, none) / `float` | `double` / `float` | |
| `boolean` | `bool` | |
| `string` | `string` | `maxLength: N` → `[D(+N)]` |
| `string` `date-time` | `DateTime` | AdHoc's own time type instead of text |
| `string` `date` | `class Date : DateTimeDef` with a precision of one day | 3 bytes instead of 8 |
| `string` `time` | `class TimeOfDay : Duration`, 24 hours in milliseconds | the time of a day is the time elapsed since midnight |
| `string` `duration` | `class DurationDefault : Duration` | |
| `string` `uuid`, `ipv6` | `class Uuid { ulong hi; ulong lo; }` | 16 bytes instead of 36 characters, and - unlike an array - usable as an item of a list; `[Validate]` keeps the text pattern for forms |
| `string` `ipv4` / `mac` | alias `IPv4` = `uint`, `Mac` = 6 bytes of a `long` | |
| `string` `binary`, `byte`, `contentEncoding: base64` | `Binary[,,]` with `[D(maxLength)]` | no size in the spec: a cap marked `TODO` |
| `string`, any other format (`email`, `uri`, …) | `string` with `[Format("email")]` | |
| `array` | `T[,,]` (list); `minItems == maxItems` → `T[]`; nullable items → `T?[,,]` | `maxItems: N` → `[D(N)]` |
| `array` with `uniqueItems` | `Set<T>` | `maxItems: N` → `[D(+N)]`; what the items say goes to `[Key: …]` |
| map | `Map<string, T>` | `maxProperties: N` → `[D(+N)]`; what the values say goes to `[Val: …]` |
| `minimum` and `maximum` | `[MinMax(min, max)]` | bit-packed by the generator; on a list, a Set or a Map it constrains the elements |
| `minimum` only, integer | `[A(min)]` | a floor with rare excursions up: varint from the low end |
| `maximum` only, integer | `[V(max)]`; unsigned: `[MinMax(0, max)]` | varint from the ceiling |
| one-sided bound of a `byte` | `[MinMax]` completed with the bound of the type | varint is for integers wider than one byte |
| `minimum == maximum` | a doc line `constant: …` | not a range |
| `pattern`, `minLength` | `[Validate(Regex = …, MinLength = …)]` | hints for forms, not wire semantics |
| `default` | `[Default(value)]` | |
| `multipleOf`, `minItems`, `minProperties`, one-sided bound of a float | a doc line `constraint: …` | |

**Nesting.** AdHoc takes a field of the form `T`, `T[]`, `T[][]`, `Set<T | T[]>`, `Map<K, T | T[]>` or an array of
a Set or a Map, and nothing deeper. JSON nests as it likes, so a level that does not fit (a map of maps, a list
of lists of lists) travels as a small pack of its own with a single `value` field, declared next to the field
and named `<Field>_Item` or `<Field>_Value`.

## Coverage of the specification

Object by object, the fields of OpenAPI 3.2 (which include those of 3.1 and 3.0; a Swagger 2.0 document is
brought to the same model by the reader). **mapped** - becomes AdHoc structure; **kept** - survives as an
attribute, a constant or a doc line; **dropped** - has no meaning outside HTTP or text, and nothing is lost that
a binary protocol could use.

| Object | Mapped | Kept as metadata | Dropped |
|:--|:--|:--|:--|
| OpenAPI | `paths`, `webhooks`, `components`, `servers` | `openapi` (file header), `info`, `security`, `tags`, `externalDocs`, extensions | `$self`, `jsonSchemaDialect` |
| Info, Contact, License | - | `title`, `summary`, `description`, `termsOfService`, `contact.*`, `license.*`, `version`, extensions | - |
| Server, Server Variable | the `Server` host | `url`, `description`, `name`, variables with `enum` / `default` / `description` | - |
| Components | `schemas`, `responses`, `parameters`, `requestBodies`, `headers`, `securitySchemes` | - | `examples`, `links`, `callbacks`, `pathItems`, `mediaTypes` that nothing refers to (referenced ones are resolved in place) |
| Path Item | every operation (`get` … `trace`, `query`, `additionalOperations`), `parameters` | `summary`, `description`, `servers` | - |
| Operation | `operationId`, `parameters`, `requestBody`, `responses`, `callbacks` | `tags`, `summary`, `description`, `externalDocs`, `deprecated`, `security`, `servers`, extensions | - |
| Parameter | `name`, `required`, `schema`, `content` | `in`, `description`, `deprecated`, `example`, `examples`, extensions | `style`, `explode`, `allowReserved`, `allowEmptyValue`: how a value is written into a URL |
| Request Body | `content`, the body itself | `description`, `required` (on a reusable body) | - |
| Media Type | `schema`, `itemSchema` | the media type of raw bytes (`[ContentType]`), the names of media types not carried | `encoding`, `prefixEncoding`, `itemEncoding`, `example`, `examples` |
| Encoding | - | - | all of it: how a part of a form is serialised |
| Responses, Response | every status, `default`, `content`, numeric `headers`, `links` | `summary`, `description`, string headers | - |
| Callback | the push and its acknowledgements | the URL expression, `summary` / `description` of the callback operation | - |
| Example | - | the `value` of a parameter example | `summary`, `description`, `dataValue`, `serializedValue`, `externalValue` |
| Link | `operationId`, `operationRef` (a transition to the linked call) | `parameters`, `requestBody`, `description` | `server` |
| Header | `schema` (numeric → header pack), `required` | `description`, `deprecated`, `example` | `style`, `explode` |
| Tag | - | `name`, `description`, `summary`, `parent`, `kind`, `externalDocs`, extensions | - |
| Reference | `$ref` | `description` written next to it | `summary` |
| Schema | `type`, `format`, `enum`, `const`, `properties`, `required`, `additionalProperties`, `patternProperties`, `items`, `uniqueItems`, `allOf`, `oneOf`, `anyOf`, `nullable`, `minimum`, `maximum`, `exclusive…`, `maxLength`, `maxItems`, `minItems` = `maxItems`, `maxProperties`, `readOnly`, `writeOnly`, `contentEncoding` | `title`, `description`, `default`, `deprecated`, `example(s)`, `externalDocs`, `pattern`, `minLength`, `multipleOf`, `minItems`, `minProperties`, `not`, extensions | `xml`; `prefixItems`, `if` / `then` / `else`, `dependentRequired`, `unevaluatedProperties`, `$defs`, `$dynamicRef`, `contentMediaType`, `contentSchema`: the reader does not hand them over or AdHoc has no form for them - such a value is carried as it is typed, or as raw bytes |
| Discriminator | `mapping` (inheritance on an `allOf` base) | `propertyName`, the mapped values, `defaultMapping` | - |
| XML | - | - | all of it |
| Security Scheme | - | `type`, `description`, `name`, `in`, `scheme`, `bearerFormat`, `openIdConnectUrl`, `deprecated`, extensions | `oauth2MetadataUrl` |
| OAuth Flows, OAuth Flow | - | `implicit`, `password`, `clientCredentials`, `authorizationCode`, `deviceAuthorization` with their URLs; `scopes` as an enum | - |
| Security Requirement | - | the scheme and its scopes, on the operation or on the API | - |

`tests/coverage.yaml` uses each mapped and kept feature once; its description is `tests/AdHoc/coverage.cs`.
What only OpenAPI 3.2 has is in `tests/oas32.yaml`.

## Names

A name comes from the document and cannot always stay as it is. A description cannot hold:

- a keyword of any target language as a name - handled the way the agent does it (`type` → `Type`);
- a field and a nested type with one name (`ImageInspect.RootFS` the field and the pack of its inline object),
  two fields with one name (`path` from the URL and `path` from the query), a member named like its type;
- sibling types that differ only by case (`PublicError` / `publicError`): their generated files collide on
  Windows;
- a type with the name of a type of an enclosing scope: inside, the name would mean the nested one. Segments of
  paths repeat all the time (`/teams` and `/access-control/teams`), and so do inline
  types (`Pet.Status` next to a schema `Status`);
- an operation named like the segment it is declared in (`GET /search` with the `operationId` `search`): its doc
  keeps the `operationId` of the document;
- two packs of different kinds with one name: every pack is declared in the project, where a schema and a
  response may both be called `NotFound`;
- a type called like something the description itself refers to (`File`, `Set`, `Duration`, `DateTime`,
  `Client`, `Server`) or like a C# contextual keyword that cannot name a type (`file`, `record`).

Names are settled in one pass, top-down, when the whole model exists. **Of two names in conflict the deeper one
changes**, so the outer, more visible name keeps the spelling of the document. **It changes the way the agent
renames a keyword: its lowercase letters go upper case one after another until the conflict is gone**
(`teams` → `Teams` → `TEams`, `RootFS` → `ROotFS`). Only names that differ by nothing but case cannot be told
apart that way and get a number (`publicError2`). Until that pass nothing holds the name of a pack as text:
references are objects, so a rename reaches every use.

In the project the schemas are named first: their names are the ones the authors of the document chose. A pack
of another kind that wants a name a schema has is told apart by its kind - `NotFound_Response`, `Pet_Body`,
`api_key_Security` - which says more than a letter case or a number would.

Other rules: a character that cannot be in an identifier becomes `_` (a `.` becomes `ˍ`, so `io.k8s.Pod` stays
readable), leading and trailing underscores are trimmed (`_links` → `links`), a leading digit gets an `N`
(`2.0` → `N2ˍ0`).

## What is dropped or approximated

- **Media types.** AdHoc is one binary format: of `application/json`, `application/xml`, … one schema is picked
  (JSON first). When another media type describes something else, the doc of the pack names it.
- **How a parameter is serialised** (`style`, `explode`, `allowReserved`). Where it travels is kept as a note.
- **HTTP status codes** survive as the names of the reply packs (`listPets_200`, `Code_404`), not as numbers on
  the wire. Several statuses that share one schema `$ref` are one reply - unless they are named responses of
  `components.responses`, which stay apart.
- **`oneOf` of unrelated primitive types written as `type: [string, integer]`**: raw bytes with a note. Written
  as `oneOf`, the same thing is a pack of alternatives.
- **Varint physics.** OpenAPI states ranges but never distributions, so `[X]` is never emitted and `[A]` / `[V]`
  only for a one-sided integer bound. Whether your counters cluster low is your call, and it is the most
  valuable hand edit after conversion; a file with 64-bit integers says so where it opens.
- **Collection bounds.** Specs rarely declare them. `_DefaultMaxLengthOf` is set to 65 535 so nothing is cut
  silently at AdHoc's default of 255; tighten it and put `[D]` on the fields whose real bound you know.
- **String constants over 1000 characters** (a long `description`, an `example`) are cut at 1000 characters,
  and a comment above says so; the document has the whole text. A `pattern` that long is left out: a pattern
  cut short is another pattern.
- **An OAuth scheme with one scope**: the enum of its scopes gets a filler member, since an enum needs two.
- **External `$ref`s** to other files are not followed: bundle a multi-file document first.

Everything dropped at a specific place leaves a comment at that place.

## Build & run

[.NET SDK 10](https://dotnet.microsoft.com/download) is required. The two NuGet packages are restored by the build.

```bash
./fetch-samples.sh        # downloads the upstream documents into samples/
./build.sh                # compiles the converter (→ out/) and converts samples/ → AdHoc/
./validate.sh AdHoc       # AdHocAgent parse-only check of every generated file (nothing is uploaded)
```

By hand:

```bash
dotnet build OpenAPI2AdHoc.csproj -c Release -o out
dotnet out/OpenAPI2AdHoc.dll api.yaml                 # → ./AdHoc/api.cs
dotnet out/OpenAPI2AdHoc.dll specs/ /some/dir         # every .json / .yaml / .yml of the folder → /some/dir
AdHocAgent.exe AdHoc/api.cs                           # then feed a result to AdHocAgent
```

CLI contract: `<input file or folder> [output folder]`; the output folder defaults to `<cwd>/AdHoc`; one `.cs`
per document. Problems the OpenAPI reader finds in a document are printed and the document is converted anyway.
The exit code is 2 when an input could not be converted at all; the reason the reader gives is printed.

## Validating the output

`validate.sh` runs AdHocAgent with `ADHOC_PARSE_ONLY=1` on a copy of every `.cs` (the agent rewrites the file in
place) and prints `OK` when the agent exits with 0 and reports no ERR / WRN. It looks for `AdHocAgent.exe` on
`PATH`, then in a sibling build of the agent; set `AGENT=/path/to/AdHocAgent.exe` to point it elsewhere.

Result for the shipped samples - **13 of 13 OK**:

| Sample | Version | Source lines | Generated lines | RPC methods | Actors |
|:--|:--|--:|--:|--:|--:|
| [`api-with-examples`](https://github.com/OAI/learn.openapis.org/blob/main/examples/v3.0/api-with-examples.yaml) | 3.0 | 178 | 116 | 2 | 0 |
| [`callback-example`](https://github.com/OAI/learn.openapis.org/blob/main/examples/v3.0/callback-example.yaml) | 3.0 | 62 | 127 | 0 | 1 |
| [`docker-engine`](https://github.com/moby/moby/blob/master/api/swagger.yaml) | 2.0 | 14 089 | 12 500 | 107 | 1 |
| [`grafana`](https://github.com/grafana/grafana/blob/main/public/openapi3.json) | 3.0 | 27 715 | 14 101 | 314 | 0 |
| [`link-example`](https://github.com/OAI/learn.openapis.org/blob/main/examples/v3.0/link-example.yaml) | 3.0 | 203 | 248 | 0 | 6 |
| [`non-oauth-scopes`](https://github.com/OAI/learn.openapis.org/blob/main/examples/v3.1/non-oauth-scopes.yaml) | 3.1 | 18 | 84 | 1 | 0 |
| [`petstore`](https://github.com/OAI/learn.openapis.org/blob/main/examples/v3.0/petstore.yaml) | 3.0 | 119 | 172 | 3 | 0 |
| [`petstore-expanded`](https://github.com/OAI/learn.openapis.org/blob/main/examples/v3.0/petstore-expanded.yaml) | 3.0 | 156 | 200 | 4 | 0 |
| [`swagger-petstore-v2`](https://petstore.swagger.io/v2/swagger.json) | 2.0 | 1 (minified JSON) | 842 | 20 | 0 |
| [`swagger-petstore-v3`](https://github.com/swagger-api/swagger-petstore/blob/master/src/main/resources/openapi.yaml) | 3.0 | 839 | 723 | 19 | 0 |
| [`tictactoe`](https://github.com/OAI/learn.openapis.org/blob/main/examples/v3.1/tictactoe.yaml) | 3.1 | 236 | 310 | 2 | 2 |
| [`uspto`](https://github.com/OAI/learn.openapis.org/blob/main/examples/v3.0/uspto.yaml) | 3.0 | 210 | 247 | 3 | 0 |
| [`webhook-example`](https://github.com/OAI/learn.openapis.org/blob/main/examples/v3.1/webhook-example.yaml) | 3.1 | 34 | 91 | 1 | 0 |

The one Actor of `docker-engine` is `GET /events`, which answers with `application/jsonl`: its events are a
stream, not a reply.

`tests/` holds four synthetic documents, all **OK**; run them after changing the converter:

```bash
dotnet out/OpenAPI2AdHoc.dll tests tests/AdHoc && ./validate.sh tests/AdHoc
```

| Document | What it is for |
|:--|:--|
| [`coverage.yaml`](tests/coverage.yaml) | walks through the specification: every feature the converter maps or keeps, once |
| [`edge-cases.yaml`](tests/edge-cases.yaml) | every name conflict the converter settles and every shape it has to rewrite: a schema called `File`, `Client` next to `client`, a field named like its pack, `/search/{path}/search`, three levels of lists, a map of maps, enum values that are not identifiers, a nullable enum that lists `null`, a constant written as a range, an object declared empty, a union with a discriminator |
| [`oas32.yaml`](tests/oas32.yaml) | what OpenAPI 3.2 added: the `QUERY` method and `additionalOperations`, `itemSchema` (a stream of items), the tag hierarchy, the device authorization flow, a named server; and a parent named twice, by `allOf` and by its own discriminator |
| [`schemas-only.yaml`](tests/schemas-only.yaml) | a document without operations: its packs still get a connection |

The converter was also run over large public documents that are not shipped here (megabytes each). Together
they convert in a few seconds:

| Document | Size | Generated lines | RPC methods | Imported parameters | Constants | Validation |
|:--|--:|--:|--:|--:|--:|:--|
| [GitHub REST API](https://github.com/github/rest-api-description) | 9.9 MB | 152 818 | 1 232 | 3 148 | 331 | OK |
| [Stripe API](https://github.com/stripe/openapi) | 6.6 MB | 128 007 | 612 | 0 | 765 | not validated |
| [Kubernetes](https://github.com/kubernetes/kubernetes/tree/master/api/openapi-spec) (Swagger 2.0) | 4.5 MB | 80 573 | 1 202 | 7 152 | 0 | OK |
| [Asana](https://github.com/Asana/openapi) | 3.2 MB | 25 887 | 251 | 615 | 2 | OK |
| [Slack Web API](https://github.com/slackapi/slack-api-specs) (Swagger 2.0) | 1.2 MB | 11 886 | 174 | 0 | 348 | OK |

Fields that end up as raw bytes because the document gives them no type: 289 in the five documents together
(GitHub 191, Slack 61, Kubernetes 24, Asana 9, Stripe 4).

**Stripe.** The description is generated and compiles, but has not been validated: Stripe's objects refer to one
another in circles (a customer has subscriptions, a subscription has a customer and invoices, an invoice has
both ...).
