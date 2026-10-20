import { Plus, Search, Trash2, X } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { toast } from 'sonner'
import { useCreateNote, useDeleteNote, useMe, useMoveNote, useNote, useNoteSearch, useNotesTree, useRestoreNote } from '@/api/queries'
import { MoveDialog, TrashDialog } from '@/components/notes/NoteDialogs'
import { NoteEditor } from '@/components/notes/NoteEditor'
import { NoteTree } from '@/components/notes/NoteTree'
import { Button } from '@/components/ui/button'
import { Card, CardContent, Empty, Skeleton } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { childrenOf, colorClass } from '@/lib/notes'
import { cn } from '@/lib/utils'

/** Notes: the tree on the left, the open note on the right. On a phone the tree and the note take turns. */
export function NotesPage() {
  const { id: param } = useParams()
  const id = param ? Number(param) : undefined
  const navigate = useNavigate()
  const me = useMe()
  const canEdit = me.data?.roles.some((r) => r === 'admin' || r === 'member') ?? false
  const tree = useNotesTree()
  const note = useNote(id)
  const create = useCreateNote()
  const move = useMoveNote()
  const remove = useDeleteNote()
  const restore = useRestoreNote()
  const [q, setQ] = useState('')
  const search = useNoteSearch(q)
  const [moving, setMoving] = useState<number | null>(null)
  const [trash, setTrash] = useState(false)
  const nodes = tree.data ?? []

  function add(parentId: number | null) {
    create.mutate(
      { title: t('notes.new'), parentId },
      { onSuccess: (created) => navigate(`/notes/${created.id}`), onError: () => toast.error(t('errors.generic')) },
    )
  }

  function onMove(noteId: number, parentId: number | null, index: number) {
    move.mutate({ id: noteId, parentId, index }, { onError: () => toast.error(t('errors.generic')) })
  }

  function onDelete(noteId: number) {
    const title = nodes.find((n) => n.id === noteId)?.title ?? ''
    if (!window.confirm(t('notes.deleteConfirm').replace('{title}', title))) return
    remove.mutate(noteId, {
      onSuccess: () => {
        if (id != null && (id === noteId || isUnder(id, noteId))) navigate('/notes')
        toast(t('notes.deleted'), { action: { label: t('notes.undo'), onClick: () => restore.mutate(noteId) } })
      },
      onError: () => toast.error(t('errors.generic')),
    })
  }

  function isUnder(child: number, parent: number) {
    const byId = new Map(nodes.map((n) => [n.id, n]))
    for (let p = byId.get(child)?.parentId; p != null; p = byId.get(p)?.parentId) if (p === parent) return true
    return false
  }

  const searching = q.trim().length > 0

  return (
    <div className="grid gap-4 md:grid-cols-[minmax(240px,300px)_1fr]">
      <aside className={cn('space-y-3', id != null && 'hidden md:block')}>
        <div className="flex items-center justify-between gap-2">
          <h1 className="text-lg font-semibold">{t('notes.title')}</h1>
          {canEdit && (
            <Button size="sm" onClick={() => add(null)} disabled={create.isPending}>
              <Plus />
              {t('notes.new')}
            </Button>
          )}
        </div>

        <label className="relative block">
          <Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-neutral-500" />
          <input
            value={q}
            onChange={(e) => setQ(e.target.value)}
            placeholder={t('notes.search')}
            className="h-10 w-full rounded-lg border border-neutral-800 bg-neutral-900 pl-9 pr-9 text-sm outline-none focus:border-amber-400"
          />
          {searching && (
            <button type="button" onClick={() => setQ('')} className="absolute right-2 top-1/2 -translate-y-1/2 p-1 text-neutral-500" aria-label={t('notes.close')}>
              <X className="size-4" />
            </button>
          )}
        </label>

        <Card>
          <CardContent className="p-2">
            {tree.isLoading && <Skeleton className="h-40" />}
            {searching ? (
              <ul className="space-y-1">
                {search.data?.length === 0 && <Empty>{t('notes.noResults')}</Empty>}
                {search.data?.map((hit) => (
                  <li key={hit.id}>
                    <Link to={`/notes/${hit.id}`} className={cn('flex gap-2 rounded-lg px-2 py-2 hover:bg-neutral-800/60', hit.id === id && 'bg-neutral-800')}>
                      <span className={cn('mt-0.5 h-4 w-1 shrink-0 rounded-full', colorClass(hit.color) ?? 'bg-transparent')} />
                      <span className="min-w-0">
                        <span className="block truncate text-sm">{hit.icon} {hit.title}</span>
                        {hit.path.length > 0 && <span className="block truncate text-xs text-neutral-500">{hit.path.map((p) => p.title).join(' / ')}</span>}
                        {hit.snippet && <span className="line-clamp-2 text-xs text-neutral-400">{hit.snippet}</span>}
                      </span>
                    </Link>
                  </li>
                ))}
              </ul>
            ) : (
              <>
                {!tree.isLoading && nodes.length === 0 && <Empty>{t('notes.empty')}</Empty>}
                <NoteTree
                  nodes={nodes}
                  selectedId={id}
                  canEdit={canEdit}
                  onMove={onMove}
                  onAddChild={(parentId) => add(parentId)}
                  onMoveTo={setMoving}
                  onDelete={onDelete}
                />
              </>
            )}
          </CardContent>
        </Card>

        <Button variant="ghost" size="sm" onClick={() => setTrash(true)} className="text-neutral-400">
          <Trash2 />
          {t('notes.trash')}
        </Button>
      </aside>

      <section className={cn('min-w-0', id == null && 'hidden md:block')}>
        {id == null && (
          <Card>
            <CardContent>
              <Empty>{nodes.length ? t('notes.pick') : t('notes.empty')}</Empty>
            </CardContent>
          </Card>
        )}
        {id != null && note.isLoading && <Skeleton className="h-96" />}
        {id != null && note.isError && (
          <Card>
            <CardContent>
              <Empty>{t('notes.noResults')}</Empty>
            </CardContent>
          </Card>
        )}
        {note.data && note.data.id === id && (
          <NoteEditor key={note.data.id} note={note.data} canEdit={canEdit} onDelete={() => onDelete(note.data.id)} />
        )}
      </section>

      {moving != null && (
        <MoveDialog
          nodes={nodes}
          id={moving}
          onMove={(parentId) => {
            onMove(moving, parentId, childrenOf(nodes).get(parentId)?.filter((n) => n.id !== moving).length ?? 0)
            setMoving(null)
            toast.success(t('notes.moved'))
          }}
          onClose={() => setMoving(null)}
        />
      )}
      {trash && <TrashDialog canEdit={canEdit} onClose={() => setTrash(false)} />}
    </div>
  )
}
