# Client generation

Two generators consume `src/Host/TaskFlow.Api/openapi-doc/TaskFlow.Api.json`, a build artifact
committed to git so both run offline (no running server needed): **Refitter** produces the shared
`.NET` Refit client (`src/UI/TaskFlow.ApiClient`), and **openapi-typescript** produces the React
client's type layer (`src/UI/TaskFlow.React/src/api/types.ts`).

## 1. Regenerate the OpenAPI document

`TaskFlow.Api.csproj` sets `OpenApiGenerateDocuments=true` but deliberately leaves
`OpenApiGenerateDocumentsOnBuild=false`: this host's `Program.cs` runs its real startup path (EF
migrations, FlowEngine workflow-JSON seeding, column-encryption key validation, CORS config
validation) to build the document, via `Microsoft.Extensions.ApiDescription.Server`'s
`dotnet-getdocument` tool invoking `HostFactoryResolver` against the compiled app. Generating on
every ordinary build/test run would make every project that references `TaskFlow.Api` require a
reachable database and encryption keys just to compile. Regeneration is therefore an explicit step,
run only when the HTTP contract changes:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:AiServices__DisableFoundryLocal = "true"
# Shared, non-secret test keys (Test.Support/TestColumnEncryption.cs) - never used outside dev/test.
$env:Database__Encryption__LocalKeyBase64 = "VGFza0Zsb3dUZXN0Q29sdW1uRW5jcnlwdGlvbktleSE="
$env:Database__Encryption__BlindIndexKeyBase64 = "VGFza0Zsb3dUZXN0QmxpbmRJbmRleEhtYWNLZXkhISE="
dotnet build src/Host/TaskFlow.Api/TaskFlow.Api.csproj -t:GenerateOpenApiDocuments
```

Requires a reachable database (whatever `dotnet run`/the Aspire AppHost normally targets) since
`RunStartupTasks` runs EF migrations and FlowEngine workflow seeding before the document generator
reads the mapped endpoints.

`tests/Test.Endpoints/OpenApiEndpointTests.Given_CommittedOpenApiDocument_When_ComparedToRuntimeDocument_Then_Matches`
is the drift guard: it fetches `/openapi/v1.json` from an in-memory `WebApplicationFactory` and
compares it (JSON-structural, `servers` stripped since that block is synthesized per-request and
absent from the build-time document) against the committed file. A forgotten regeneration after an
endpoint change fails this test, not silently.

## 2. Regenerate the .NET Refit client

```powershell
dotnet tool run refitter -- --settings-file src/UI/TaskFlow.ApiClient/.refitter --no-banner
```

`src/UI/TaskFlow.ApiClient/.refitter` pins every generator option (see the file itself - Refitter
writes its full effective settings back out, so it stays self-documenting). Notable choices:

- `generateContracts: false` (`--interface-only`) plus `additionalNamespaces` pointing at
  `TaskFlow.Application.Models(.Paging/.Reads)`, `TaskFlow.Domain.Shared.Enums`, and
  `EF.Common.Contracts`: every **non-generic** DTO/filter/enum in the generated interface resolves
  directly to the real shared type Blazor/React/Uno/the endpoint tests already use - Refitter never
  emits its own copy of `TaskItemDto`, `CategoryDto`, etc.
- **Generic envelopes** (`DefaultResponse<T>`, `DefaultRequest<T>`, `CursorPage<T>`,
  `EF.Common.Contracts.PagedResponse<T>`/`SearchRequest<T>`) have no OpenAPI equivalent - the spec
  flattens each closed instantiation to its own schema (`DefaultResponseOfTaskItemDto`, etc; verified
  against the generated document, not guessed). `GeneratedTypeAliases.cs` is a small hand-written
  file of `global using X = Y<Z>;` aliases mapping every flattened name back to the real generic
  type. This is a stronger result than Refitter's own documented fallback ("keep its own DTOs
  internal, map at the edge"): there is no second DTO set and no mapping code at all, because a
  closed generic type alias is a real, load-bearing alias, not a wrapper class. Regenerating may
  introduce a new `XOfY` name (a new entity, or a new generic wrapper) - `dotnet build` will fail
  with an unresolved-type error naming it; add one alias line.
- `includeTags: [TaskItems, Categories, Tags, Attachments]` deliberately excludes the `Comments` and
  `ChecklistItems` tags: those tag the *standalone* `/comments`, `/checklist-items` routes
  (`CommentEndpoints.cs`, `ChecklistItemEndpoints.cs`), which no client here uses - comments and
  checklist items are mutated only through the TaskItem aggregate root's nested routes
  (`/task-items/{id}/comments`, `.../checklist-items`), which are tagged `TaskItems` and so are
  already included.
- Every mapped endpoint (both the Service-style and CQRS-style route files) now calls `.WithName(...)`
  with a stable operation name (`SearchTaskItems`, `UpdateCategory`, `RemoveTaskItemComment`, ...).
  Metadata only - it does not change the wire contract - but without it ASP.NET Core's OpenAPI
  generation invents Swagger-generator-style names (`CategoriesGET`, `Search2`, `TagsPOST2`, ...)
  that are unreadable as generated C# method names.

`POST /api/v1/attachments/upload` is a known generation gap: OpenAPI describes its file part as a
`multipart/form-data` schema property (`"type": "string", "format": "binary"`), and Refitter's
`--interface-only` mode recognizes the `[Multipart]` shape for the surrounding scalar fields but
drops the file part entirely - the generated `UploadAttachment` method can never actually send a
file. It is left in the generated file (harmless, unused) rather than hand-patched, so regeneration
stays this one command. The real upload path is hand-written: `IAttachmentUploadClient` (its own
interface, not a second partial of `ITaskFlowApiClient`), using Refit's `StreamPart`. That shape is
also a case the Refit *source generator* cannot build (`RF006`, suppressed at that one method with
a comment) - `IAttachmentUploadClient` is registered via the reflection-based `AddRefitClient`,
never `AddRefitGeneratedClient`, so the fallback the analyzer warns about is the intended path.

## 3. Regenerate the React types

```bash
cd src/UI/TaskFlow.React
npm run gen:api
```

Runs `openapi-typescript ../../Host/TaskFlow.Api/openapi-doc/TaskFlow.Api.json -o src/api/types.ts`.
TypeScript's structural typing means the same "no open generics in OpenAPI" limitation that needs
type aliases in C# needs nothing at all here: `client.ts` reaches into
`components["schemas"][...]`/`paths[...]["...")]["requestBody"|"responses"]` per operation, and a
flattened `DefaultResponseOfTaskItemDto`-equivalent shape is structurally identical to a hand-written
`DefaultResponse<TaskItemDto>` type, so no aliasing step is needed on this side.

## Tool versions

- `refitter` 2.1.3, pinned in `dotnet-tools.json` (repo-local, `dotnet tool restore`).
- `openapi-typescript` 7.13.0, pinned as a `devDependency` in `src/UI/TaskFlow.React/package.json`.
- `Microsoft.Extensions.ApiDescription.Server` 10.0.11 (matches the `Microsoft.AspNetCore.OpenApi`
  pin), `PackageVersion` in `Directory.Packages.props`.
