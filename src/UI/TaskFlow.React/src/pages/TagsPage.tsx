import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Box,
  Button,
  IconButton,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import { Edit, Plus, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { isPreconditionFailed, taskFlowApi } from '../api/client'
import { queryKeys } from '../api/queryKeys'
import type { TagDto } from '../api/models'
import { useNotifications } from '../app/notificationContext'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { PageHeader } from '../components/PageHeader'
import { ErrorState, LoadingState } from '../components/StateViews'

const defaultColors = ['#0ea5e9', '#22c55e', '#f59e0b', '#ef4444', '#8b5cf6', '#64748b']

/** Renders the tags page and coordinates its data operations. */
export function TagsPage() {
  const queryClient = useQueryClient()
  const { showNotification } = useNotifications()
  const [editing, setEditing] = useState<TagDto>(() => emptyTag())
  const [deleteTarget, setDeleteTarget] = useState<TagDto | null>(null)

  // Full list, not a search page: /task-metadata is uncapped by the [1,100] search PageSize clamp
  // (up to PageSizeLimits.MetadataMax), which a PageSize=500 search would now reject.
  const metadataQuery = useQuery({
    queryKey: queryKeys.metadata,
    queryFn: ({ signal }) => taskFlowApi.getTaskMetadata(signal),
  })

  const saveMutation = useMutation({
    mutationFn: (tag: TagDto) => (tag.id ? taskFlowApi.updateTag(tag) : taskFlowApi.createTag(tag)),
    onSuccess: async () => {
      showNotification(editing.id ? 'Tag saved.' : 'Tag created.', 'success')
      setEditing(emptyTag())
      await queryClient.invalidateQueries({ queryKey: queryKeys.metadata })
    },
    onError: (error) => {
      if (isPreconditionFailed(error)) {
        showNotification('Tag changed elsewhere, reloading.', 'warning')
        void queryClient.invalidateQueries({ queryKey: queryKeys.metadata })
        return
      }
      showNotification(error instanceof Error ? error.message : 'Tag save failed.', 'error')
    },
  })

  const deleteMutation = useMutation({
    mutationFn: taskFlowApi.deleteTag,
    onSuccess: async () => {
      showNotification('Tag deleted.', 'success')
      setDeleteTarget(null)
      await queryClient.invalidateQueries({ queryKey: queryKeys.metadata })
    },
    onError: (error) => {
      setDeleteTarget(null)
      if (isPreconditionFailed(error)) {
        showNotification('Tag changed elsewhere, reloading.', 'warning')
        void queryClient.invalidateQueries({ queryKey: queryKeys.metadata })
        return
      }
      showNotification(error instanceof Error ? error.message : 'Tag delete failed.', 'error')
    },
  })

  /** Renders save tag page helper UI and keeps form or display state consistent. */
  function saveTag() {
    if (!editing.name.trim()) {
      showNotification('Name is required.', 'warning')
      return
    }
    saveMutation.mutate({ ...editing, color: editing.color || defaultColors[0], name: editing.name.trim() })
  }

  const tags = metadataQuery.data?.tags ?? []

  return (
    <>
      <PageHeader
        title="Tags"
        actions={
          <Button onClick={() => setEditing(emptyTag())} startIcon={<Plus size={18} />} variant="contained">
            New Tag
          </Button>
        }
      />

      <Box sx={{ display: 'grid', gap: 2.5, gridTemplateColumns: { xs: '1fr', lg: 'minmax(0, 1fr) 340px' } }}>
        <Stack spacing={2}>
          {metadataQuery.isLoading ? <LoadingState label="Loading tags" /> : null}
          {metadataQuery.isError ? (
            <ErrorState error={metadataQuery.error} onRetry={() => void metadataQuery.refetch()} />
          ) : null}

          {tags.length > 0 ? (
            <Paper variant="outlined" sx={{ overflow: 'hidden' }}>
              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell>Name</TableCell>
                    <TableCell>Color</TableCell>
                    <TableCell align="right">Actions</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {tags.map((tag) => (
                    <TableRow hover key={tag.id ?? tag.name}>
                      <TableCell>
                        <Typography sx={{ fontWeight: 650 }}>{tag.name}</Typography>
                      </TableCell>
                      <TableCell>
                        <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                          <Box
                            aria-hidden="true"
                            sx={{
                              bgcolor: tag.color || defaultColors[0],
                              borderRadius: '50%',
                              height: 18,
                              width: 18,
                            }}
                          />
                          <Typography color="text.secondary">{tag.color || defaultColors[0]}</Typography>
                        </Stack>
                      </TableCell>
                      <TableCell align="right">
                        <Tooltip title="Edit tag">
                          <IconButton aria-label="Edit tag" onClick={() => setEditing(tag)} size="small">
                            <Edit size={18} />
                          </IconButton>
                        </Tooltip>
                        <Tooltip title="Delete tag">
                          <IconButton
                            aria-label="Delete tag"
                            color="error"
                            onClick={() => setDeleteTarget(tag)}
                            size="small"
                          >
                            <Trash2 size={18} />
                          </IconButton>
                        </Tooltip>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </Paper>
          ) : null}
        </Stack>

        <Paper variant="outlined" sx={{ alignSelf: 'start', p: 2.5 }}>
          <Typography variant="h2">{editing.id ? 'Edit Tag' : 'New Tag'}</Typography>
          <Stack spacing={2} sx={{ mt: 2 }}>
            <TextField
              label="Name"
              onChange={(event) => setEditing((current) => ({ ...current, name: event.target.value }))}
              required
              value={editing.name}
            />
            <TextField
              label="Color"
              onChange={(event) => setEditing((current) => ({ ...current, color: event.target.value }))}
              type="color"
              value={editing.color || defaultColors[0]}
            />
            <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap' }}>
              {defaultColors.map((color) => (
                <Tooltip key={color} title={color}>
                  <IconButton
                    aria-label={`Use ${color}`}
                    onClick={() => setEditing((current) => ({ ...current, color }))}
                    sx={{
                      bgcolor: color,
                      border: editing.color === color ? 2 : 0,
                      borderColor: 'text.primary',
                      height: 30,
                      width: 30,
                      '&:hover': { bgcolor: color },
                    }}
                  />
                </Tooltip>
              ))}
            </Stack>
            <Stack direction="row" spacing={1}>
              <Button disabled={saveMutation.isPending} onClick={saveTag} variant="contained">
                Save
              </Button>
              <Button onClick={() => setEditing(emptyTag())}>Reset</Button>
            </Stack>
          </Stack>
        </Paper>
      </Box>

      <ConfirmDialog
        message={`Delete '${deleteTarget?.name ?? 'this tag'}'? This cannot be undone.`}
        onCancel={() => setDeleteTarget(null)}
        onConfirm={() => deleteTarget && deleteMutation.mutate(deleteTarget)}
        open={deleteTarget !== null}
        title="Delete tag"
      />
    </>
  )
}

/** Creates the default tag form state for add and edit flows. */
function emptyTag(): TagDto {
  return {
    color: defaultColors[0],
    name: '',
  }
}
