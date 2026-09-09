import { Link as RouterLink, useNavigate, useParams } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Accordion,
  AccordionDetails,
  AccordionSummary,
  Box,
  Button,
  Checkbox,
  Divider,
  IconButton,
  MenuItem,
  Paper,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import { ArrowLeft, ChevronDown, Plus, Save, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { isPreconditionFailed, taskFlowApi } from '../api/client'
import { queryKeys } from '../api/queryKeys'
import type { ChecklistItemDto, CommentDto, Priority, TaskItemDto, TaskItemStatus } from '../api/models'
import { priorities, taskStatuses } from '../api/models'
import { useNotifications } from '../app/notificationContext'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { PageHeader } from '../components/PageHeader'
import { ErrorState, LoadingState } from '../components/StateViews'
import { formatDate, fromDateInputValue, toDateInputValue } from '../utils/format'

/** Renders the task editor page and coordinates its data operations. */
export function TaskEditorPage() {
  const { id } = useParams()
  const isCreate = !id

  const taskQuery = useQuery({
    enabled: !isCreate && Boolean(id),
    queryKey: queryKeys.task(id),
    queryFn: ({ signal }) => taskFlowApi.getTask(id!, signal),
  })

  if (taskQuery.isLoading) {
    return (
      <>
        <PageHeader title={isCreate ? 'New Task' : 'Edit Task'} eyebrow="Tasks" />
        <LoadingState label="Loading task" />
      </>
    )
  }

  if (taskQuery.isError) {
    return (
      <>
        <PageHeader title={isCreate ? 'New Task' : 'Edit Task'} eyebrow="Tasks" />
        <ErrorState error={taskQuery.error} onRetry={() => void taskQuery.refetch()} />
      </>
    )
  }

  const initialTask = isCreate ? createEmptyTask() : taskQuery.data ? normalizeTask(taskQuery.data) : null

  return initialTask ? (
    <TaskEditorContent initialTask={initialTask} isCreate={isCreate} routeId={id} />
  ) : null
}

/** Describes task editor content props data used by the React UI. */
interface TaskEditorContentProps {
  initialTask: TaskItemDto
  isCreate: boolean
  routeId?: string
}

/** Renders the task editor form and coordinates task save, checklist, and comment mutations. */
function TaskEditorContent({ initialTask, isCreate, routeId }: TaskEditorContentProps) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { showNotification } = useNotifications()
  const [form, setForm] = useState<TaskItemDto>(() => initialTask)
  const [startDate, setStartDate] = useState(() => toDateInputValue(initialTask.startDate))
  const [dueDate, setDueDate] = useState(() => toDateInputValue(initialTask.dueDate))
  const [newChecklistTitle, setNewChecklistTitle] = useState('')
  const [newCommentBody, setNewCommentBody] = useState('')
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [childBusy, setChildBusy] = useState(false)

  const metadataQuery = useQuery({
    queryKey: queryKeys.metadata,
    queryFn: ({ signal }) => taskFlowApi.getTaskMetadata(signal),
  })

  const saveMutation = useMutation({
    mutationFn: (item: TaskItemDto) => (isCreate ? taskFlowApi.createTask(item) : taskFlowApi.updateTask(item)),
    onSuccess: async (saved) => {
      showNotification(isCreate ? 'Task created.' : 'Task saved.', 'success')
      await queryClient.invalidateQueries({ queryKey: ['tasks'] })
      await queryClient.invalidateQueries({ queryKey: queryKeys.dashboard })
      await queryClient.invalidateQueries({ queryKey: queryKeys.task(saved.id ?? routeId) })
      if (isCreate && saved.id) {
        navigate(`/tasks/${saved.id}`, { replace: true })
      } else {
        setForm(normalizeTask(saved))
        setStartDate(toDateInputValue(saved.startDate))
        setDueDate(toDateInputValue(saved.dueDate))
      }
    },
    onError: (error) => {
      if (isPreconditionFailed(error)) {
        showNotification('Task changed elsewhere, reloading.', 'warning')
        void reloadTask()
        return
      }
      showNotification(error instanceof Error ? error.message : 'Task save failed.', 'error')
    },
  })

  const deleteMutation = useMutation({
    mutationFn: taskFlowApi.deleteTask,
    onSuccess: async () => {
      showNotification('Task deleted.', 'success')
      await queryClient.invalidateQueries({ queryKey: ['tasks'] })
      await queryClient.invalidateQueries({ queryKey: queryKeys.dashboard })
      navigate('/tasks', { replace: true })
    },
    onError: (error) => {
      if (isPreconditionFailed(error)) {
        showNotification('Task changed elsewhere, reloading.', 'warning')
        void reloadTask()
        return
      }
      showNotification(error instanceof Error ? error.message : 'Task delete failed.', 'error')
    },
  })

  const categories = metadataQuery.data?.categories ?? []
  const selectedCategory = categories.find((category) => category.id === form.categoryId)
  const checklist = form.checklistItems ?? []
  const comments = form.comments ?? []
  const isBusy = saveMutation.isPending || deleteMutation.isPending || childBusy

  function saveTask() {
    if (!form.title.trim()) {
      showNotification('Title is required.', 'warning')
      return
    }

    saveMutation.mutate({
      ...form,
      dueDate: fromDateInputValue(dueDate),
      startDate: fromDateInputValue(startDate),
      title: form.title.trim(),
    })
  }

  function setField<TKey extends keyof TaskItemDto>(key: TKey, value: TaskItemDto[TKey]) {
    setForm((current) => ({ ...current, [key]: value }))
  }

  /** Re-fetches the task directly (not through the react-query cache) after a child mutation, since
   *  each one bumps the aggregate root's Version - the next If-Match this form sends must stay current. */
  async function reloadTask() {
    if (!form.id) return
    const fresh = await taskFlowApi.getTask(form.id)
    setForm(normalizeTask(fresh))
    setStartDate(toDateInputValue(fresh.startDate))
    setDueDate(toDateInputValue(fresh.dueDate))
    await queryClient.invalidateQueries({ queryKey: queryKeys.task(form.id) })
  }

  // Children are mutated through the aggregate root's dedicated nested routes (GR-15), never bundled
  // into the whole-task PUT, and each mutation bumps the root Version - so every handler below reloads
  // the task afterward rather than patching local state.
  async function addChecklistItem() {
    const title = newChecklistTitle.trim()
    if (!title || !form.id) return
    setChildBusy(true)
    try {
      await taskFlowApi.addChecklistItem(form.id, { title, sortOrder: checklist.length })
      setNewChecklistTitle('')
      await reloadTask()
    } catch (error) {
      showNotification(error instanceof Error ? error.message : 'Failed to add checklist item.', 'error')
    } finally {
      setChildBusy(false)
    }
  }

  async function toggleChecklistItem(item: ChecklistItemDto, completed: boolean) {
    if (!form.id) return
    setChildBusy(true)
    try {
      await taskFlowApi.updateChecklistItem(form.id, {
        ...item,
        completedDate: completed ? new Date().toISOString() : null,
        isCompleted: completed,
      })
      await reloadTask()
    } catch (error) {
      if (isPreconditionFailed(error)) {
        showNotification('Checklist item changed elsewhere, reloading.', 'warning')
        await reloadTask()
      } else {
        showNotification(error instanceof Error ? error.message : 'Failed to update checklist item.', 'error')
      }
    } finally {
      setChildBusy(false)
    }
  }

  async function removeChecklistItem(item: ChecklistItemDto) {
    if (!form.id) return
    setChildBusy(true)
    try {
      await taskFlowApi.removeChecklistItem(form.id, item)
      await reloadTask()
    } catch (error) {
      if (isPreconditionFailed(error)) {
        showNotification('Checklist item changed elsewhere, reloading.', 'warning')
        await reloadTask()
      } else {
        showNotification(error instanceof Error ? error.message : 'Failed to remove checklist item.', 'error')
      }
    } finally {
      setChildBusy(false)
    }
  }

  async function addComment() {
    const body = newCommentBody.trim()
    if (!body || !form.id) return
    setChildBusy(true)
    try {
      await taskFlowApi.addComment(form.id, { body })
      setNewCommentBody('')
      await reloadTask()
    } catch (error) {
      showNotification(error instanceof Error ? error.message : 'Failed to add comment.', 'error')
    } finally {
      setChildBusy(false)
    }
  }

  async function removeComment(comment: CommentDto) {
    if (!form.id) return
    setChildBusy(true)
    try {
      await taskFlowApi.removeComment(form.id, comment)
      await reloadTask()
    } catch (error) {
      if (isPreconditionFailed(error)) {
        showNotification('Comment changed elsewhere, reloading.', 'warning')
        await reloadTask()
      } else {
        showNotification(error instanceof Error ? error.message : 'Failed to remove comment.', 'error')
      }
    } finally {
      setChildBusy(false)
    }
  }

  return (
    <>
      <PageHeader
        title={isCreate ? 'New Task' : 'Edit Task'}
        eyebrow="Tasks"
        actions={
          <>
            <Button component={RouterLink} startIcon={<ArrowLeft size={18} />} to="/tasks">
              Back
            </Button>
            {!isCreate ? (
              <Button
                color="error"
                disabled={isBusy}
                onClick={() => setConfirmDelete(true)}
                startIcon={<Trash2 size={18} />}
              >
                Delete
              </Button>
            ) : null}
            <Button disabled={isBusy} onClick={saveTask} startIcon={<Save size={18} />} variant="contained">
              Save
            </Button>
          </>
        }
      />

      <Box sx={{ display: 'grid', gap: 2.5, gridTemplateColumns: { xs: '1fr', lg: 'minmax(0, 1fr) 340px' } }}>
          <Stack spacing={2.5}>
            <Paper variant="outlined" sx={{ p: 2.5 }}>
              <Stack spacing={2}>
                <TextField
                  autoFocus
                  fullWidth
                  label="Title"
                  onChange={(event) => setField('title', event.target.value)}
                  required
                  value={form.title}
                />
                <TextField
                  fullWidth
                  label="Description"
                  minRows={4}
                  multiline
                  onChange={(event) => setField('description', event.target.value)}
                  value={form.description ?? ''}
                />
                <Box
                  sx={{
                    display: 'grid',
                    gap: 2,
                    gridTemplateColumns: { xs: '1fr', md: 'repeat(2, minmax(0, 1fr))' },
                  }}
                >
                  <TextField
                    label="Status"
                    onChange={(event) => setField('status', event.target.value as TaskItemStatus)}
                    select
                    value={form.status}
                  >
                    {taskStatuses.map((status) => (
                      <MenuItem key={status} value={status}>
                        {status === 'InProgress' ? 'In Progress' : status}
                      </MenuItem>
                    ))}
                  </TextField>
                  <TextField
                    label="Priority"
                    onChange={(event) => setField('priority', event.target.value as Priority)}
                    select
                    value={form.priority}
                  >
                    {priorities.map((priority) => (
                      <MenuItem key={priority} value={priority}>
                        {priority}
                      </MenuItem>
                    ))}
                  </TextField>
                  <TextField
                    label="Start Date"
                    onChange={(event) => setStartDate(event.target.value)}
                    slotProps={{ inputLabel: { shrink: true } }}
                    type="date"
                    value={startDate}
                  />
                  <TextField
                    label="Due Date"
                    onChange={(event) => setDueDate(event.target.value)}
                    slotProps={{ inputLabel: { shrink: true } }}
                    type="date"
                    value={dueDate}
                  />
                  <TextField
                    label="Category"
                    onChange={(event) => setField('categoryId', event.target.value || null)}
                    select
                    value={form.categoryId ?? ''}
                  >
                    <MenuItem value="">None</MenuItem>
                    {categories.map((category) => (
                      <MenuItem key={category.id ?? category.name} value={category.id ?? ''}>
                        {category.name}
                      </MenuItem>
                    ))}
                  </TextField>
                  <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' }}>
                    <TextField
                      label="Est. Effort"
                      onChange={(event) =>
                        setField('estimatedEffort', event.target.value ? Number(event.target.value) : null)
                      }
                      type="number"
                      value={form.estimatedEffort ?? ''}
                    />
                    <TextField
                      label="Actual Effort"
                      onChange={(event) =>
                        setField('actualEffort', event.target.value ? Number(event.target.value) : null)
                      }
                      type="number"
                      value={form.actualEffort ?? ''}
                    />
                  </Box>
                </Box>
              </Stack>
            </Paper>

            <Accordion defaultExpanded={!isCreate}>
              <AccordionSummary expandIcon={<ChevronDown size={18} />}>
                <Typography variant="h3">Checklist ({checklist.length})</Typography>
              </AccordionSummary>
              <AccordionDetails>
                <Stack spacing={1.5}>
                  {isCreate ? (
                    <Typography color="text.secondary">Save the task before adding checklist items.</Typography>
                  ) : (
                    <>
                      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1}>
                        <TextField
                          fullWidth
                          onChange={(event) => setNewChecklistTitle(event.target.value)}
                          onKeyDown={(event) => {
                            if (event.key === 'Enter') {
                              event.preventDefault()
                              void addChecklistItem()
                            }
                          }}
                          placeholder="Add item"
                          value={newChecklistTitle}
                        />
                        <Button
                          disabled={childBusy}
                          onClick={() => void addChecklistItem()}
                          startIcon={<Plus size={17} />}
                          variant="contained"
                        >
                          Add
                        </Button>
                      </Stack>
                      {checklist.map((item) => (
                        <Stack direction="row" key={item.id} spacing={1} sx={{ alignItems: 'center' }}>
                          <Checkbox
                            checked={item.isCompleted}
                            disabled={childBusy}
                            onChange={(event) => void toggleChecklistItem(item, event.target.checked)}
                          />
                          <Typography
                            sx={{
                              flex: 1,
                              textDecoration: item.isCompleted ? 'line-through' : 'none',
                            }}
                          >
                            {item.title}
                          </Typography>
                          <Tooltip title="Remove checklist item">
                            <IconButton
                              aria-label="Remove checklist item"
                              color="error"
                              disabled={childBusy}
                              onClick={() => void removeChecklistItem(item)}
                            >
                              <Trash2 size={17} />
                            </IconButton>
                          </Tooltip>
                        </Stack>
                      ))}
                    </>
                  )}
                  {!isCreate && checklist.length === 0 ? (
                    <Typography color="text.secondary">No checklist items yet.</Typography>
                  ) : null}
                </Stack>
              </AccordionDetails>
            </Accordion>

            <Accordion defaultExpanded={!isCreate}>
              <AccordionSummary expandIcon={<ChevronDown size={18} />}>
                <Typography variant="h3">Comments ({comments.length})</Typography>
              </AccordionSummary>
              <AccordionDetails>
                <Stack spacing={1.5}>
                  {isCreate ? (
                    <Typography color="text.secondary">Save the task before adding comments.</Typography>
                  ) : (
                    <>
                      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1}>
                        <TextField
                          fullWidth
                          minRows={2}
                          multiline
                          onChange={(event) => setNewCommentBody(event.target.value)}
                          placeholder="Add a comment"
                          value={newCommentBody}
                        />
                        <Button
                          disabled={childBusy}
                          onClick={() => void addComment()}
                          startIcon={<Plus size={17} />}
                          variant="contained"
                        >
                          Add
                        </Button>
                      </Stack>
                      {comments.map((comment) => (
                        <Paper key={comment.id} variant="outlined" sx={{ p: 1.5 }}>
                          <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-start' }}>
                            <Typography sx={{ flex: 1, whiteSpace: 'pre-wrap' }}>{comment.body}</Typography>
                            <Tooltip title="Remove comment">
                              <IconButton
                                aria-label="Remove comment"
                                color="error"
                                disabled={childBusy}
                                onClick={() => void removeComment(comment)}
                              >
                                <Trash2 size={17} />
                              </IconButton>
                            </Tooltip>
                          </Stack>
                        </Paper>
                      ))}
                    </>
                  )}
                  {!isCreate && comments.length === 0 ? (
                    <Typography color="text.secondary">No comments yet.</Typography>
                  ) : null}
                </Stack>
              </AccordionDetails>
            </Accordion>
          </Stack>

          <Paper variant="outlined" sx={{ alignSelf: 'start', p: 2.5 }}>
            <Typography variant="h3">Info</Typography>
            <Divider sx={{ my: 1.5 }} />
            <Stack spacing={1}>
              <InfoRow label="ID" value={form.id ?? '(new)'} />
              <InfoRow label="Category" value={selectedCategory?.name ?? '-'} />
              <InfoRow label="Completed" value={formatDate(form.completedDate)} />
            </Stack>
          </Paper>
      </Box>

      <ConfirmDialog
        message={`Delete '${form.title || 'this task'}'? This cannot be undone.`}
        onCancel={() => setConfirmDelete(false)}
        onConfirm={() => {
          setConfirmDelete(false)
          if (form.id) deleteMutation.mutate(form)
        }}
        open={confirmDelete}
        title="Delete task"
      />
    </>
  )
}

/** Renders a label-value row for task editor metadata. */
function InfoRow({ label, value }: { label: string; value: string }) {
  return (
    <Box sx={{ display: 'grid', gap: 1, gridTemplateColumns: '96px minmax(0, 1fr)' }}>
      <Typography color="text.secondary">{label}</Typography>
      <Typography sx={{ overflowWrap: 'anywhere' }}>{value}</Typography>
    </Box>
  )
}

/** Builds create empty task values for API or UI code. */
function createEmptyTask(): TaskItemDto {
  return {
    checklistItems: [],
    comments: [],
    description: '',
    priority: 'Medium',
    status: 'Open',
    title: '',
  }
}

/** Normalizes task form state into the API payload shape. */
function normalizeTask(task: TaskItemDto): TaskItemDto {
  return {
    ...task,
    checklistItems: [...(task.checklistItems ?? [])].sort((left, right) => left.sortOrder - right.sortOrder),
    comments: task.comments ?? [],
  }
}
