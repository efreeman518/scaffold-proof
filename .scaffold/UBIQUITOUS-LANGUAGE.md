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
| `Version` | concept | App-managed monotonic `long` concurrency token on every entity; the raw value behind an ETag. | `long Version` column, `IsConcurrencyToken()`. See D-021. |
| `Aggregate Version` | concept | The root entity's `Version`; the single concurrency currency for an aggregate, bumped whenever a child is mutated. | Child PUT/DELETE use the root's `Version` as If-Match currency, not their own. See D-031. |
| `Cursor` | concept | Opaque, tamper-protected token encoding the last-seen sort key and id for keyset (seek) pagination. | `CursorToken`, `ICursorProtector`; Base64Url encoded. |
| `SortMode` | concept | Named, enumerated sort order for a cursor-paged list (e.g. `DueDateAsc`, `ModifiedDesc`). | `TaskItemSortMode` enum; part of the cursor's tamper check. |
| `Export` | concept | Bulk, unpaged NDJSON stream of an entity's flat scalar fields for downstream/offline consumption. | `TaskItemExportDto`; `StreamTaskItemExportAsync(afterId, batchSize)`. |
| `Idempotent Create` | concept | A create request carrying a caller-supplied UUIDv7 id; replay with an equivalent payload returns the existing entity, a divergent payload conflicts. | See GR-17, D-033. |
| `Outbox` | concept | TaskFlow-owned transactional table of staged domain events/messages, written in the same transaction as the domain change and drained by the scheduler host. | `OutboxMessage`; provider-neutral lease-based claim. See D-026. |
| `Consumer Inbox` | concept | Table recording which messages a given consumer has already processed, enforcing at-least-once-safe idempotent consumption. | `ConsumerInbox (Consumer, MessageId, ProcessedAtUtc)`. See D-029. |
| `Work Table` | concept | A staging table holding units of deferred work for a background worker (e.g. blob deletes) that is not itself the domain entity. | `BlobDeleteWork`; drained by a dedicated worker. |
| `Lease` | concept | A time-bounded claim (`LeaseOwner`, `LeaseExpiresUtc`) a scheduler/worker replica takes on a batch of rows so other replicas skip them until it expires. | Conditional `ExecuteUpdateAsync` claim. See D-026. |
| `Recurrence Template` | concept | The Recurring `TaskItem` definition (`RecurrencePattern`, `NextOccurrenceAtUtc`) that a scheduled job expands into cloned occurrence tasks. | `RecurrenceTemplateId` on the generated occurrence. |
| `Occurrence` | concept | One cloned instance of a Recurrence Template for a specific point in time, unique per tenant on `(RecurrenceTemplateId, OccurrenceUtc)`. | `OccurrenceUtc`; upserted via FlexLabs Upsert. |
| `Provider` (database provider) | concept | The relational database engine backing a DbContext for a given deployment: SQL Server or PostgreSQL, selected by config. | `Database:Provider`; `TaskFlowDbProvider` enum. See D-020. |
| `Blind Index` | concept | An indexed HMAC-SHA256 hash sibling column enabling equality lookup on a deterministically-encrypted value without decrypting it. | `SecureDeterministicHash`. See D-023. |

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
| `TaskItemStatusChanged` | `TaskItem` | Task lifecycle state changed. | Service Bus, Functions, Cosmos projection, notifications. |
| `TaskItemCompleted` | `TaskItem` | Task reached completed state. | Notifications. |
| `TaskItemRescheduled` | `TaskItem` | Task date range changed. | Recalculation, scheduler. |
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
- Use integration event records in `Application.Contracts.Events`; do not publish domain namespace events over transport.
- Use `OwnerType` and `OwnerId` for polymorphic attachment ownership; do not add EF navigation collections to owners.
- Use `WorkflowDefinition` for the persisted FlowEngine document; reserve unqualified `Workflow` for prose, never as a C# type name.
- Use `HumanTask` (not `Task`) for FlowEngine human-approval records - collides with both `System.Threading.Tasks.Task` and `TaskItem`.
