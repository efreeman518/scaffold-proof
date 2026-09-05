export const queryKeys = {
  dashboard: ['dashboard'] as const,
  tasks: (filters: unknown) => ['tasks', filters] as const,
  task: (id: string | undefined) => ['task', id] as const,
  // One key for both Categories and Tags: GET /task-metadata returns both lists in one call.
  metadata: ['task-metadata'] as const,
}
