// Thin, hand-written alias layer over the generated ../api/types.ts (openapi-typescript, regenerated
// by `npm run gen:api` - see docs/plans/client-generation.md). OpenAPI has no concept of an open
// generic type, so Microsoft.AspNetCore.OpenApi flattens every closed instantiation of
// DefaultResponse<T>/CursorPage<T>/etc to its own schema (DefaultResponseOfTaskItemDto, ...). Unlike
// the C# Refit client, this needs no alias trick to compile against those flattened names - TypeScript's
// structural typing means `components["schemas"]["DefaultResponseOfTaskItemDto"]` is already
// interchangeable with a hand-written `DefaultResponse<TaskItemDto>` wherever the shapes match.
//
// Two genuine wire quirks do need a normalizing layer, so the types below are not bare re-exports:
// 1. .NET 10's OpenAPI generator types every integer property (int32 and int64 alike) as
//    `number | string` (verified in the committed document: "version": {"type": ["null","integer",
//    "string"], "format": "int64"}) - a defensive schema for clients that serialize big numbers as
//    strings. The wire value this API actually sends is always a JSON number. Re-declaring these
//    fields as plain `number` here keeps every page's arithmetic/display code simple; client.ts
//    coerces on the rare path where a string could arrive.
// 2. Nothing in the schema is marked `required` (C# reference-type properties are optional to the
//    generator unless annotated), so every field comes back optional. Re-declaring the handful each
//    page actually depends on unconditionally (title, name, status, ...) avoids an undefined-check on
//    every access; client.ts still guards before sending a request that needs one of them.
import type { components } from './types'

export type Id = string

export type TaskItemDto = Omit<
  components['schemas']['TaskItemDto'],
  'title' | 'status' | 'priority' | 'version' | 'checklistItems' | 'comments'
> & {
  title: string
  status: TaskItemStatus
  priority: Priority
  version?: number | null
  checklistItems?: ChecklistItemDto[] | null
  comments?: CommentDto[] | null
}

export type CategoryDto = Omit<components['schemas']['CategoryDto'], 'name' | 'sortOrder' | 'version'> & {
  name: string
  sortOrder: number
  version?: number | null
}

export type TagDto = Omit<components['schemas']['TagDto'], 'name' | 'version'> & {
  name: string
  version?: number | null
}

export type CommentDto = Omit<components['schemas']['CommentDto'], 'body' | 'version'> & {
  body: string
  version?: number | null
}

export type ChecklistItemDto = Omit<
  components['schemas']['ChecklistItemDto'],
  'title' | 'isCompleted' | 'sortOrder' | 'version'
> & {
  title: string
  isCompleted: boolean
  sortOrder: number
  version?: number | null
}

export type TaskItemSearchFilter = components['schemas']['TaskItemSearchFilter']
export type CategorySearchFilter = components['schemas']['CategorySearchFilter']
export type TagSearchFilter = components['schemas']['TagSearchFilter']

export type TaskItemStatus = NonNullable<components['schemas']['TaskItemStatus']>
export type Priority = NonNullable<components['schemas']['Priority']>
export type TaskItemSortMode = NonNullable<components['schemas']['TaskItemSortMode']>

export type TaskItemCursorSearchRequest = components['schemas']['TaskItemCursorSearchRequest']
export type TaskItemCursorPage = Omit<components['schemas']['CursorPageOfTaskItemDto'], 'items'> & {
  items: TaskItemDto[]
}

export type TaskItemSummaryDto = Omit<components['schemas']['TaskItemSummaryDto'], 'byStatus' | 'total'> & {
  byStatus: { status: TaskItemStatus; count: number }[]
  total: number
}

export type TaskMetadataDto = Omit<components['schemas']['TaskMetadataDto'], 'categories' | 'tags'> & {
  categories: CategoryDto[]
  tags: TagDto[]
}

// HttpValidationProblemDetails is a superset of ProblemDetails (adds `errors`); the API returns either
// shape depending on error kind, so this is the type problem-message parsing works against.
export type ProblemDetails = components['schemas']['HttpValidationProblemDetails']

// UI pickers exclude the unset sentinel value; the wire type still allows it since the server accepts
// filters that omit Status/Priority entirely.
export const taskStatuses = ['Open', 'InProgress', 'Blocked', 'Completed', 'Cancelled'] as const
export const priorities = ['Low', 'Medium', 'High', 'Critical'] as const
