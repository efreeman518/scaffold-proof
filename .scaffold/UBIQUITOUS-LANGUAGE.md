# Ubiquitous Language - TaskFlow

This file records the shared domain language used by the TaskFlow reference app. Use these terms in code, tests, API contracts, docs, prompts, and future AI sessions.

## Purpose

- Business domain: multi-tenant task management.
- Primary users: TaskFlow users and tenant administrators.
- Success criteria: users can organize, track, discuss, complete, search, and automate task work inside tenant boundaries.

## Accepted Terms

| Term | Type | Meaning | Code/Naming Guidance |
|---|---|---|---|
| `TaskFlow` | system | Multi-tenant task management platform. | Use as solution and namespace root. |
| `TaskItem` | aggregate | Core work item managed by a tenant. | Use `TaskItem`; avoid `Task`. |
| `Category` | entity | Tenant-scoped hierarchy for grouping task items. | Use `Category`, `CategoryTree`, `ParentCategoryId`. |
| `Tag` | entity | Lightweight tenant-scoped label assignable to many task items. | Use `Tag`; many-to-many bridge is `TaskItemTag`. |
| `Comment` | child entity | Discussion entry owned by a task item. | Use `Comment`; belongs to one `TaskItem`. |
| `ChecklistItem` | child entity | Ordered completion step owned by a task item. | Use `ChecklistItem`; not `Todo`. |
| `Attachment` | entity | File or link metadata owned by a task item or comment. | Use `OwnerType` + `OwnerId`; no parent navigation collection. |
| `TaskItemTag` | join entity | Explicit many-to-many bridge between task item and tag. | Use as a real entity when association metadata is needed. |
| `DateRange` | value-object | Start and due date pair for a task item. | EF owned value object on `TaskItem`. |
| `RecurrencePattern` | value-object | Recurrence interval, frequency, and end conditions. | EF owned value object on `TaskItem`. |
| `Secure Property` | concept | A domain property whose column is protected at rest with SQL Always Encrypted. TaskFlow demonstrates `SecureDeterministic` and `SecureRandom` on `TaskItem`. | `string?` domain property, `varbinary(200)` column via UTF8 value converter. See D-019. |
| `Always Encrypted` | pattern | SQL Server client-side column encryption: the driver encrypts/decrypts; the server only stores ciphertext. | Enabled per-connection via `Column Encryption Setting=Enabled`; keys managed in Azure Key Vault. |
| `Column Master Key (CMK)` | concept | The key-encrypting key for Always Encrypted, stored in Azure Key Vault as an RSA key. | `CMK_WITH_AKV`; created in a migration via `EF.Data.MigrationSupport`. |
| `Column Encryption Key (CEK)` | concept | The data-encrypting key, itself encrypted by the CMK and stored as SQL metadata. | `CEK_WITH_AKV`. |
| `Deterministic Encryption` | concept | Always Encrypted mode producing identical ciphertext for identical plaintext; supports equality lookups. | Used by `SecureDeterministic`. |
| `Randomized Encryption` | concept | Always Encrypted mode producing non-repeatable ciphertext; not queryable. | Used by `SecureRandom`. |
| `GlobalAdmin` | role | Cross-tenant administrator. | May bypass tenant-specific checks where explicitly allowed. |
| `TenantAdmin` | role | Administrator inside one tenant. | Use for tenant-scoped administration. |
| `TenantMember` | role | Normal authenticated tenant user. | Use for tenant-scoped user actions. |
| `EntraID` | external-system | Enterprise identity provider for API authentication. | Use in auth configuration. |
| `EntraExternal` | external-system | External identity provider for gateway/user-facing auth. | Use for gateway auth configuration. |
| `enterprise` | auth scenario | Internal workforce authentication scenario. | Use in domain spec auth scenario. |
| `ETag` | concept | HTTP entity tag identifying a specific version of an aggregate; used for optimistic concurrency via `If-Match`. | `ETag: "<Version>"` (strong). See `Version`, `Aggregate Version`. |
| `Version` | concept | App-managed monotonic `long` concurrency token on every entity; the raw value behind an ETag. | `long Version` column, `IsConcurrencyToken()`. Now provided by `EF.Domain.EntityBase<TId>.Version` and incremented by `EF.Data.DbContextBase.SaveChangesAsync` (1.1.100); the insert baseline of 1 and the timestamps stay in `VersionTimestampInterceptor`. See D-021. |
| `Aggregate Version` | concept | The root entity's `Version`; the single concurrency currency for an aggregate, bumped whenever a child is mutated. | Child PUT/DELETE use the root's `Version` as If-Match currency, not their own. See D-031. |
| `Cursor` | concept | Opaque, tamper-protected token encoding the last-seen sort key and id for keyset (seek) pagination. | Now provided by `EF.Data.Contracts.CursorCodec`/`CursorPosition`/`KeysetCursor` (1.1.100), HMAC-signed and schema-versioned; Base64Url encoded. Scoped to tenant + `SortMode` by `TaskItemRepositoryQuery.CursorScope`. Paging itself is `EF.Data.Contracts.IQueryableExtensions.KeysetPageAsync` (1.1.101) over the projected DTO query. The page and request shapes are `EF.Common.Contracts.CursorPage<T>`/`CursorSearchRequest<TFilter,TSortMode>` (`Items`, not `Data`), the page-size range `EF.Common.Contracts.PageSizeLimits`. |
| `SortMode` | concept | Named, enumerated sort order for a cursor-paged list (e.g. `DueDateAsc`, `ModifiedDesc`). | `TaskItemSortMode` enum; folded into the cursor scope key, so a cursor from another mode fails closed. |
| `Export` | concept | Bulk, unpaged NDJSON stream of an entity's flat scalar fields for downstream/offline consumption. | `TaskItemExportDto`; `StreamTaskItemExportAsync(afterId, batchSize)`. |
| `Idempotent Create` | concept | A create request carrying a caller-supplied UUIDv7 id; replay with an equivalent payload returns the existing entity, a divergent payload conflicts. | See GR-17, D-033. |
| `Outbox` | concept | TaskFlow-owned transactional table of staged domain events/messages, written in the same transaction as the domain change and drained by the scheduler host. | `OutboxMessage`; provider-neutral lease-based claim. See D-026. |
| `Consumer Inbox` | concept | Table recording which messages a given consumer has already processed, enforcing at-least-once-safe idempotent consumption. | `ConsumerInbox (Consumer, MessageId, ProcessedAtUtc)`. See D-029. |
| `Work Table` | concept | A staging table holding units of deferred work for a background worker (e.g. blob deletes) that is not itself the domain entity. | `BlobDeleteWork`; drained by a dedicated worker. |
| `Lease` | concept | A time-bounded claim (`LeaseOwner`, `LeaseExpiresUtc`) a scheduler/worker replica takes on a batch of rows so other replicas skip them until it expires. | Conditional `ExecuteUpdateAsync` claim. See D-026. |
| `Recurrence Template` | concept | The Recurring `TaskItem` definition (`RecurrencePattern`, `NextOccurrenceAtUtc`) that a scheduled job expands into cloned occurrence tasks. | `RecurrenceTemplateId` on the generated occurrence. |
| `Occurrence` | concept | One cloned instance of a Recurrence Template for a specific point in time, unique per tenant on `(RecurrenceTemplateId, OccurrenceUtc)`. | `OccurrenceUtc`; upserted via `EF.Data` `IRepositoryBase.UpsertRangeAsync`. |
| `Provider` (database provider) | concept | The relational database engine backing a DbContext for a given deployment: SQL Server or PostgreSQL, selected by config. | `Database:Provider`; `TaskFlowDbProvider` enum. See D-020. |
| `Blind Index` | concept | An indexed HMAC-SHA256 hash sibling column enabling equality lookup on a deterministically-encrypted value without decrypting it. | `SecureDeterministicHash`. See D-023. |
| `Connection Multiplexer` | concept | One long-lived broker connection shared process-wide, over which channels are rented per publish and dedicated per consumer. | `RabbitMqConnectionMultiplexer`; analogous to StackExchange.Redis `ConnectionMultiplexer`. See D-034. |
| `Prefetch Count` | concept | The number of unacknowledged deliveries a broker may have outstanding on one consumer channel; also that consumer's dispatch concurrency. | `Messaging:RabbitMq:Consumers:{queue}:PrefetchCount`; `BasicQosAsync(0, n, global: false)`. See D-034. |
| `Dead-Letter Exchange` | concept | The RabbitMQ exchange a queue routes rejected or expired messages to; the broker-side equivalent of a Service Bus dead-letter queue. | `taskflow.domain-events.dlx` -> `taskflow.dead-letter`; `x-dead-letter-exchange` queue argument. See D-034. |
| `Lane` | concept | A named preset of infrastructure-provider defaults for a deployment target (e.g. full Azure, or Portable). | `TASKFLOW_LANE`; seeds provider-switch defaults only, never read at a registration site. See D-035. |
| `Portable lane` | concept | The non-Azure hosting lane: containers on a VPS, PostgreSQL and the LLM on separate providers, Azure retained only for Key Vault and App Configuration. | `TASKFLOW_LANE=Portable`. See D-035, D-036, D-044. |
| `Provider switch` | concept | One independent, provider-neutral seam (enum + config key + env var + resolver + dispatcher) selecting which concrete implementation of a port is registered. | `RegisterServices.Messaging.cs` is the template; env wins over config, unknown value falls back to the Azure default. See D-034, D-035. |
| `Lane preset` | concept | The set of provider-switch defaults a hosting lane seeds; any switch's own env/config still overrides its lane default. | `hostingLaneDefaults` in `resource-implementation.yaml`. See D-035. |
| `Read-model provider` | concept | The backing store for the denormalized `TaskView` projection: Cosmos DB or a relational table. | `ReadModel:Provider = Cosmos \| Relational`. See D-038. |
| `Object-storage provider` | concept | The backing store for attachment binary content behind `IBlobStorageRepository`: Azure Blob or S3-compatible. | `Storage:Provider = AzureBlob \| S3`. See D-037. |
| `Audit sink` | concept | The backing store for `AuditLog` rows: Azure Table or a relational table. | `Audit:Provider = AzureTable \| Relational`. See D-039. |
| `Presigned URL` | concept | A time-limited, signed download URL issued directly against object storage without proxying bytes through the app. | S3 arm `GetBlobUriAsync`; `Storage:S3:PublicServiceUrl`, `DownloadUrlLifetime`. See D-037. |
| `Feature flag` | concept | A dynamically toggleable gate on an optional surface, backed by Azure App Configuration + `Microsoft.FeatureManagement`; a disabled HTTP surface answers 404. | `TaskViews`, `Export`, `SemanticSearch`, `AiReview`; `IVariantFeatureManager`, endpoint-filter check points. See D-042, GR-20. |
| `Targeting context` | concept | The tenant identity a feature flag's targeting filter evaluates against to decide rollout. | `TenantTargetingContextAccessor`; `WithTargeting<T>()`. See D-042. |
| `Pooler mode` | concept | The PgBouncer connection-pooling mode a Postgres connection string is prepared for. | `Database:PostgreSql:PoolerMode = None \| Transaction`; `No Reset On Close=true;Max Auto Prepare=0`. See D-045. |
| `Hedged request` | concept | A second, concurrent attempt at an in-flight idempotent read issued after a delay, racing the original to reduce tail latency. | Blazor `AddHedging` on GET only; never on writes. See D-051. |
| `Distributed lock` | concept | A short-lived, cross-replica mutual-exclusion primitive for non-reentrant startup tasks, distinct from the lease pattern used for work-table claims. | `IDistributedLock.TryAcquireAsync`; Redis `SET NX PX` + Lua compare-and-delete release, in-process fallback. See D-052. |
| `Liveness probe` | concept | The health check answering whether the process itself should be restarted; excludes external dependencies. | `/healthz/live`, tag `live`, checks `self` only. See D-049. |
| `Readiness probe` | concept | The health check answering whether an instance should receive traffic; includes external dependencies. | `/healthz/ready`, tag `ready`: database, outbox, scheduler, broker on consumer hosts. See D-049. |
| `Trace context propagation` | concept | Carrying the W3C `traceparent`/`tracestate` across an asynchronous broker hop so producer and consumer spans join one trace. | Injected into RabbitMQ headers / Service Bus `ApplicationProperties` by the dispatcher; extracted by the consumer with an `ActivityLink` to the producer. See D-053. |
| `Internal RPC (gRPC read service)` | concept | The one in-cluster service-to-service hop exposed as gRPC instead of REST: Blazor Server reading task summaries directly from the Api. | `TaskFlowRead.GetTaskItemSummary/GetTaskMetadata/GetTaskItem`; dedicated cleartext HTTP/2 Kestrel endpoint, port 8081. See D-054. |
| `Semantic search` / `Embedding` | concept | Vector-similarity search over task item text, backed by a stored embedding vector; opt-in and tenant-scoped like every other search mode. | `TaskItemEmbedding.Embedding (vector(1536))`; `Search:Provider = PgVector`; `SearchMode.Semantic`. See D-040, GR-19. |

## Rejected Synonyms

| Rejected Term | Use Instead | Reason |
|---|---|---|
| `Task` | `TaskItem` | Avoid collision with `System.Threading.Tasks.Task`. |
| `Todo` | `TaskItem` or `ChecklistItem` | Too vague for aggregate vs child step. |
| `Label` | `Tag` | Reference app uses tag vocabulary. |
| `File` | `Attachment` | Attachment may be a file or external link. |

## Entities And Aggregates

| Entity | Aggregate Role | Tenant Scope | Ownership Notes |
|---|---|---|---|
| `TaskItem` | root | tenant-scoped | Owns comments, checklist items, subtasks, value objects, and status lifecycle. References category and tags. |
| `Category` | root | tenant-scoped | Self-referencing hierarchy; can contain task items. |
| `Tag` | root | tenant-scoped | Assigned to task items through `TaskItemTag`. |
| `Comment` | child | tenant-scoped | Owned by one task item. |
| `ChecklistItem` | child | tenant-scoped | Owned by one task item. |
| `Attachment` | associated entity | tenant-scoped | Owned polymorphically by task item or comment through `OwnerType` and `OwnerId`. |
| `TaskItemTag` | join entity | tenant-scoped | Bridges task item and tag. |

## Commands And Actions

| Command/Action | Actor | Target | Business Meaning | Expected Result |
|---|---|---|---|---|
| `Create` | tenant member | `TaskItem` | Start tracking a new item of work. | `TaskItemCreated` event; status becomes `Open`. |
| `Start` | tenant member | `TaskItem` | Begin work on an open task. | Status becomes `InProgress`. |
| `Block` | tenant member | `TaskItem` | Mark active work as blocked. | Status becomes `Blocked`. |
| `Unblock` | tenant member | `TaskItem` | Resume blocked work. | Status becomes `InProgress`. |
| `Complete` | tenant member | `TaskItem` | Finish task work. | Status becomes `Completed`; checklist guard must pass. |
| `Cancel` | tenant member | `TaskItem` | Stop task work without completion. | Status becomes `Cancelled`. |
| `Reopen` | tenant member | `TaskItem` | Return completed or cancelled task to active backlog. | Status becomes `Open`. |
| `Reschedule` | tenant member | `TaskItem` | Change task date range. | `TaskItemRescheduled` event. |
| `Reassign` | tenant member | `TaskItem` | Change task assignee. | Assignee changes when identity model is enabled. |

## States

| Entity | State | Meaning | Terminal |
|---|---|---|---|
| `TaskItem` | `None` | Uninitialized/default status. | no |
| `TaskItem` | `Open` | Created and not yet started. | no |
| `TaskItem` | `InProgress` | Work has started. | no |
| `TaskItem` | `Blocked` | Work cannot proceed until an obstacle is removed. | no |
| `TaskItem` | `Completed` | Work finished. | no |
| `TaskItem` | `Cancelled` | Work intentionally stopped. | no |

## Events

| Event | Raised By | Meaning | Consumers |
|---|---|---|---|
| `TaskItemCreated` | `TaskItem` | A new task item exists. | Service Bus, Functions, Cosmos projection, AI search. |
| `TaskItemContentChanged` | `TaskItem` | The task's embeddable text (title or description) actually changed value. | pgvector embedding consumer (D-040). |
| `TaskItemStatusChanged` | `TaskItem` | Task lifecycle state changed. | Service Bus, Functions, Cosmos projection, notifications. |
| `TaskItemCompleted` | `TaskItem` | Task reached completed state. | Notifications. |
| `TaskItemRescheduled` | `TaskItem` | Task date range changed. | None today - no aggregate method raises it; the record and its wire registration are kept for the schema, not for a live path. |
| `TaskItemOverdueSuspected` | Scheduler | Scheduled job found a likely overdue task. | Notifications, escalation. |
| `CommentAdded` | `Comment` | Discussion entry was added. | Notifications, activity views. |
| `AttachmentUploaded` | `Attachment` | Attachment metadata points to uploaded content. | Functions, metadata extraction. |

## Policies And Rules

| Policy/Rule | Applies To | Meaning | Decision Source |
|---|---|---|---|
| `MaxActiveTasksPerTenant` | `TaskItem` | Tenant cannot exceed configured active task quota. | D-001 |
| `ChecklistCompletionRequired` | `TaskItem`, `ChecklistItem` | A task cannot complete until checklist items are complete. | D-007 |
| `SubTaskCompletionRequired` | `TaskItem` | A parent task cannot complete until subtasks are complete. | D-007 |
| `TaskNotOverdue` | `TaskItem` | Overdue state is detected by scheduler and surfaced as a domain concern. | D-009 |
| `MaxNestingDepth` | `Category` | Category hierarchy cannot exceed five levels. | D-008 |
| `MaxSubTaskDepth` | `TaskItem` | Subtask hierarchy cannot exceed three levels. | D-007 |
| `StatusTransitionPolicy` | `TaskItem` | Allowed status moves depend on current state and requested action. | D-007 |

## External Systems

| System | Domain Meaning | Interaction Vocabulary |
|---|---|---|
| SQL Server | Authoritative transactional store. | query, transaction, migration, repository. |
| Redis | Distributed cache and cache backplane. | cache, invalidate, backplane. |
| Service Bus | Integration event transport. | publish, topic, queue, subscription. |
| Cosmos DB | Denormalized task read model. | project, reconcile, task view. |
| Blob Storage | Attachment content store. | upload, download, SAS URI. |
| Azure AI Search | Hybrid/vector task search. | index, search, embed, retrieve. |
| Azure OpenAI | Task assistant agent backing model. | chat, tool, summarize, ground. |
| Azure Key Vault | Stores the Always Encrypted Column Master Key (RSA). | sign, wrap/unwrap, Crypto User role. See D-019. |

## Orchestration Vocabulary (FlowEngine)

Terms used by the workflow orchestration layer. These are FlowEngine-runtime concepts, not domain aggregates - they sit alongside the task-management domain rather than inside it.

| Term | Type | Meaning | Code/Naming Guidance |
|---|---|---|---|
| `Workflow` | concept | A declarative graph of nodes + edges defining an orchestration. Use as the umbrella noun in prose. | Don't use `Workflow` as a code identifier - collides with the runtime concept. Use `WorkflowDefinition` for the persisted shape. |
| `WorkflowDefinition` | entity | A versioned JSON document describing a workflow (id, version, status, entry node, nodes, edges, params schema). | `EF.FlowEngine.Definition.WorkflowDefinition`. Lives in `flowengine.Workflows`. |
| `ExecutionInstance` | entity | One running or completed invocation of a workflow definition. Carries `Status`, `CorrelationId`, `Context`, `Deadline`, `Version`. | Persisted in `flowengine.Executions`. Reference by `InstanceId`. |
| `Node` | concept | A single step in a workflow - one of 19 built-in types (`agent`, `decision`, `human`, `integration`, `loop`, `message`, `output`, `query`, `document`, ...). | Refer to specific types by their `type` string; custom executors implement `INodeExecutor<TInputs,TOutputs>`. |
| `NodeExecutor` | service | The runtime handler for one node type. | All 19 built-ins auto-registered by `AddFlowEngine()` in 1.0.104+. |
| `Edge` | concept | A typed transition from one node to another, optionally guarded by a `when` expression and labeled with an outcome (`Match`, `Error`, custom). | Multiple edges from one node = branching; first satisfied `when` wins. |
| `IFlowClient` / `clientRef` | service | A typed external integration target referenced by a node's `config.clientRef`. | TaskFlow registers three: `taskflow-api` (HTTP), `integration-events` (ServiceBus), `ai-agent` (Azure OpenAI). |
| `HumanTask` | entity | A pending human approval/review produced by a `human` node. Holds `AssignedTo` (role string), `Status`, `FormData`, `DueAt`, `EscalationAt`. | Persisted in `flowengine.HumanTasks`. Surfaced in the dashboard's `/human-tasks` page. |
| `Quorum` | pattern | Human-task config requiring N-of-M approvers before the node completes. | Used by `ai-task-triage`'s critical-priority branch (2-of-3). |
| `Compensation` | pattern | An inverse node executed when a later node in the same instance faults. | Declared as `compensationNodeId` on a side-effect node (e.g. revert PATCH on saga failure). |
| `Outbox` (FlowEngine) | concern | Staging rows for `message` / `integration` / `agent` side effects, persisted by the same `SaveChangesAsync` that advances workflow state. | `flowengine.Outbox`. Distinct from app-side `AuditInterceptor` (see tech-design Section 11.4). |
| `CircuitBreaker` (FlowEngine) | concern | Per-key durable breaker state for connector calls; shared across replicas. | `flowengine.CircuitBreakers`. |
| `IWorkflowTrigger` | service | TaskFlow-side interface for invoking workflows from domain events. | `Application.MessageHandlers.WorkflowTriggerHandler`. Not auto-fired today - see Section 14.6. |

### Rejected Synonyms (Orchestration)

| Rejected Term | Use Instead | Reason |
|---|---|---|
| `Workflow` (as code identifier) | `WorkflowDefinition` | Avoid collision with the runtime concept; reserve unqualified `Workflow` for prose. |
| `Job` | `ExecutionInstance` | "Job" collides with TickerQ scheduled jobs; FlowEngine instances are not cron-triggered. |
| `Step` | `Node` | FlowEngine's own vocabulary; consistent with the JSON schema. |
| `Task` (FlowEngine) | `HumanTask` for human approvals, `Node` for engine steps | Prevents triple collision with `System.Threading.Tasks.Task` and `TaskItem`. |

## Naming Notes

- Use `TaskItem` everywhere source-level naming needs the aggregate; do not shorten it to `Task`.
- Use `Attachment` for metadata and blob reference. Do not model file bytes on the domain entity.
- Use the shared lifecycle event records in `Domain.Shared.Events` as both the raised domain event and the integration-event payload (one record, wrapped by `IntegrationEventEnvelope`); do not define a second parallel set.
- Use integration event records raised by the aggregate; do not publish domain namespace events over transport.
- Use `OwnerType` and `OwnerId` for polymorphic attachment ownership; do not add EF navigation collections to owners.
- Use `WorkflowDefinition` for the persisted FlowEngine document; reserve unqualified `Workflow` for prose, never as a C# type name.
- Use `HumanTask` (not `Task`) for FlowEngine human-approval records - collides with both `System.Threading.Tasks.Task` and `TaskItem`.
