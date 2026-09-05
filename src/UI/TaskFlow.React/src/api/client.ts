import type {
  CategoryDto,
  ChecklistItemDto,
  CommentDto,
  Id,
  ProblemDetails,
  TagDto,
  TaskItemCursorPage,
  TaskItemCursorSearchRequest,
  TaskItemDto,
  TaskItemSummaryDto,
  TaskMetadataDto,
} from './models'

const apiVersionRoot = '/api/v1'
const configuredBaseUrl = (import.meta.env.PROD ? import.meta.env.VITE_API_BASE_URL : '')?.replace(/\/$/, '') ?? ''

/**
 * Error shape used by React Query callers. It preserves ProblemDetails when the API returns
 * RFC 7807 payloads while still supporting plain text or empty error bodies. `status === 412`
 * (Precondition Failed) is the concurrency-conflict signal callers check for distinctly.
 */
export class ApiError extends Error {
  status: number
  details?: ProblemDetails

  constructor(message: string, status: number, details?: ProblemDetails) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.details = details
  }
}

/** True when the error is the optimistic-concurrency conflict (D-021): the resource changed elsewhere. */
export function isPreconditionFailed(error: unknown): error is ApiError {
  return error instanceof ApiError && error.status === 412
}

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  // Production calls the Aspire-provided gateway URL. Development leaves the origin empty so
  // Vite proxying can route API traffic without baking a dynamic port into the bundle.
  const response = await fetch(`${configuredBaseUrl}${path}`, {
    ...init,
    credentials: 'include',
    headers: {
      Accept: 'application/json',
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...init?.headers,
    },
  })

  if (response.status === 204) {
    return undefined as T
  }

  const payload = await readJson(response)

  if (!response.ok) {
    const problem = payload as ProblemDetails | undefined
    throw new ApiError(problemMessage(problem, response), response.status, problem)
  }

  return payload as T
}

/** Reads a JSON response body when the API returns content. */
async function readJson(response: Response): Promise<unknown> {
  const text = await response.text()
  if (!text) {
    return undefined
  }

  try {
    return JSON.parse(text)
  } catch {
    return text
  }
}

/** Builds a user-facing API error message from problem details. */
function problemMessage(problem: ProblemDetails | undefined, response: Response): string {
  if (problem?.detail) return problem.detail
  if (problem?.title) return problem.title
  if (problem?.errors) {
    const first = Object.values(problem.errors).flat()[0]
    if (first) return first
  }
  return `Request failed with ${response.status} ${response.statusText}`
}

function post<TResponse, TBody>(path: string, body: TBody, signal?: AbortSignal): Promise<TResponse> {
  return requestJson<TResponse>(path, {
    method: 'POST',
    body: JSON.stringify(body),
    signal,
  })
}

/** Sends a PUT with the required If-Match precondition (D-021/GR-16): "*" overrides unconditionally. */
function put<TResponse, TBody>(
  path: string,
  body: TBody,
  ifMatch: string | undefined,
  signal?: AbortSignal,
): Promise<TResponse> {
  return requestJson<TResponse>(path, {
    method: 'PUT',
    body: JSON.stringify(body),
    headers: { 'If-Match': ifMatch ?? '*' },
    signal,
  })
}

/** Sends a DELETE with the required If-Match precondition and validates the expected empty response. */
function del(path: string, ifMatch: string | undefined, signal?: AbortSignal): Promise<void> {
  return requestJson<void>(path, { method: 'DELETE', headers: { 'If-Match': ifMatch ?? '*' }, signal })
}

// Minimal API write endpoints return DefaultResponse<T>. Treat null item as a contract
// failure because callers need a concrete entity for cache invalidation and navigation.
function unwrap<T>(response: { item: T | null }): T {
  if (!response.item) {
    throw new ApiError('The API returned an empty response.', 500)
  }
  return response.item
}

/** Formats a DTO's optimistic-concurrency Version as the If-Match header value ("*" when unset). */
function ifMatchOf(entity: { version?: number | null }): string {
  return entity.version != null ? String(entity.version) : '*'
}

export const taskFlowApi = {
  // ---- TaskItems (cursor-only search, GR-18) ----
  searchTasks: (request: TaskItemCursorSearchRequest, signal?: AbortSignal) =>
    post<TaskItemCursorPage, TaskItemCursorSearchRequest>(`${apiVersionRoot}/task-items/search`, request, signal),

  getTask: async (id: Id, signal?: AbortSignal) =>
    unwrap(await requestJson<{ item: TaskItemDto | null }>(`${apiVersionRoot}/task-items/${id}`, { signal })),

  createTask: async (item: TaskItemDto) =>
    unwrap(await post<{ item: TaskItemDto | null }, { item: TaskItemDto }>(`${apiVersionRoot}/task-items`, { item })),

  updateTask: async (item: TaskItemDto) => {
    if (!item.id) throw new ApiError('Cannot update a task without an id.', 400)
    return unwrap(
      await put<{ item: TaskItemDto | null }, { item: TaskItemDto }>(
        `${apiVersionRoot}/task-items/${item.id}`,
        { item },
        ifMatchOf(item),
      ),
    )
  },

  deleteTask: (item: Pick<TaskItemDto, 'id' | 'version'>) => {
    if (!item.id) throw new ApiError('Cannot delete a task without an id.', 400)
    return del(`${apiVersionRoot}/task-items/${item.id}`, ifMatchOf(item))
  },

  getTaskSummary: (signal?: AbortSignal) =>
    requestJson<TaskItemSummaryDto>(`${apiVersionRoot}/task-items/summary`, { signal }),

  getTaskMetadata: (signal?: AbortSignal) =>
    requestJson<TaskMetadataDto>(`${apiVersionRoot}/task-metadata`, { signal }),

  // ---- TaskItem child aggregates: mutated only through the root's nested routes (GR-15) ----
  addComment: async (taskId: Id, item: Pick<CommentDto, 'body'>) =>
    unwrap(
      await post<{ item: CommentDto | null }, { item: Pick<CommentDto, 'body'> }>(
        `${apiVersionRoot}/task-items/${taskId}/comments`,
        { item },
      ),
    ),

  removeComment: (taskId: Id, comment: Pick<CommentDto, 'id' | 'version'>) => {
    if (!comment.id) throw new ApiError('Cannot remove a comment without an id.', 400)
    return del(`${apiVersionRoot}/task-items/${taskId}/comments/${comment.id}`, ifMatchOf(comment))
  },

  addChecklistItem: async (taskId: Id, item: Pick<ChecklistItemDto, 'title' | 'sortOrder'>) =>
    unwrap(
      await post<{ item: ChecklistItemDto | null }, { item: Pick<ChecklistItemDto, 'title' | 'sortOrder'> }>(
        `${apiVersionRoot}/task-items/${taskId}/checklist-items`,
        { item },
      ),
    ),

  updateChecklistItem: async (taskId: Id, item: ChecklistItemDto) => {
    if (!item.id) throw new ApiError('Cannot update a checklist item without an id.', 400)
    return unwrap(
      await put<{ item: ChecklistItemDto | null }, { item: ChecklistItemDto }>(
        `${apiVersionRoot}/task-items/${taskId}/checklist-items/${item.id}`,
        { item },
        ifMatchOf(item),
      ),
    )
  },

  removeChecklistItem: (taskId: Id, item: Pick<ChecklistItemDto, 'id' | 'version'>) => {
    if (!item.id) throw new ApiError('Cannot remove a checklist item without an id.', 400)
    return del(`${apiVersionRoot}/task-items/${taskId}/checklist-items/${item.id}`, ifMatchOf(item))
  },

  // ---- Categories (full list comes from getTaskMetadata; only mutations live here) ----
  createCategory: async (item: CategoryDto) =>
    unwrap(
      await post<{ item: CategoryDto | null }, { item: CategoryDto }>(`${apiVersionRoot}/categories`, { item }),
    ),

  updateCategory: async (item: CategoryDto) => {
    if (!item.id) throw new ApiError('Cannot update a category without an id.', 400)
    return unwrap(
      await put<{ item: CategoryDto | null }, { item: CategoryDto }>(
        `${apiVersionRoot}/categories/${item.id}`,
        { item },
        ifMatchOf(item),
      ),
    )
  },

  deleteCategory: (item: Pick<CategoryDto, 'id' | 'version'>) => {
    if (!item.id) throw new ApiError('Cannot delete a category without an id.', 400)
    return del(`${apiVersionRoot}/categories/${item.id}`, ifMatchOf(item))
  },

  // ---- Tags (full list comes from getTaskMetadata; only mutations live here) ----
  createTag: async (item: TagDto) =>
    unwrap(await post<{ item: TagDto | null }, { item: TagDto }>(`${apiVersionRoot}/tags`, { item })),

  updateTag: async (item: TagDto) => {
    if (!item.id) throw new ApiError('Cannot update a tag without an id.', 400)
    return unwrap(
      await put<{ item: TagDto | null }, { item: TagDto }>(`${apiVersionRoot}/tags/${item.id}`, { item }, ifMatchOf(item)),
    )
  },

  deleteTag: (item: Pick<TagDto, 'id' | 'version'>) => {
    if (!item.id) throw new ApiError('Cannot delete a tag without an id.', 400)
    return del(`${apiVersionRoot}/tags/${item.id}`, ifMatchOf(item))
  },
}

export const apiRuntime = {
  apiRoot: `${configuredBaseUrl}${apiVersionRoot}`,
  devProxyTarget: import.meta.env.DEV ? import.meta.env.VITE_API_BASE_URL : undefined,
}
