import { CornerDownRight, Home, RotateCcw } from 'lucide-react'
import { useMemo } from 'react'
import { toast } from 'sonner'
import type { NoteNodeDto } from '@/api/client'
import { useNotesTrash, useRestoreNote } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Empty, Skeleton } from '@/components/ui/primitives'
import { getLanguage, t } from '@/i18n'
import { branchIds, childrenOf, colorClass } from '@/lib/notes'
import { cn } from '@/lib/utils'
import { Dialog } from './Dialog'

/** "Move to…": every note outside the moved branch, indented as in the tree, plus the root. Works with a finger. */
export function MoveDialog({ nodes, id, onMove, onClose }: {
  nodes: NoteNodeDto[]
  id: number
  onMove: (parentId: number | null) => void
  onClose: () => void
}) {
  const note = nodes.find((n) => n.id === id)
  const children = useMemo(() => childrenOf(nodes), [nodes])
  const excluded = useMemo(() => branchIds(children, id), [children, id])

  const options: { node: NoteNodeDto; depth: number }[] = []
  const walk = (parentId: number | null, depth: number) => {
    for (const node of children.get(parentId) ?? []) {
      if (excluded.has(node.id)) continue
      options.push({ node, depth })
      walk(node.id, depth + 1)
    }
  }
  walk(null, 0)

  return (
    <Dialog title={t('notes.moveTitle').replace('{title}', note?.title ?? '')} onClose={onClose}>
      <Button variant="ghost" className="w-full justify-start" disabled={note?.parentId == null} onClick={() => onMove(null)}>
        <Home />
        {t('notes.moveRoot')}
      </Button>
      {options.map(({ node, depth }) => (
        <Button
          key={node.id}
          variant="ghost"
          className={cn('w-full justify-start', node.id === note?.parentId && 'text-amber-300')}
          style={{ paddingLeft: 16 + depth * 16 }}
          disabled={node.id === note?.parentId}
          onClick={() => onMove(node.id)}
        >
          <CornerDownRight className="text-neutral-600" />
          <span className={cn('h-4 w-1 shrink-0 rounded-full', colorClass(node.color) ?? 'bg-transparent')} />
          {node.icon && <span>{node.icon}</span>}
          <span className="truncate">{node.title}</span>
        </Button>
      ))}
    </Dialog>
  )
}

/** Deleted branches, newest first, each with how many notes went with it and when it goes for good. */
export function TrashDialog({ canEdit, onClose }: { canEdit: boolean; onClose: () => void }) {
  const trash = useNotesTrash(true)
  const restore = useRestoreNote()
  const date = new Intl.DateTimeFormat(getLanguage() === 'en' ? 'en-GB' : 'ru-RU', { day: 'numeric', month: 'long' })

  return (
    <Dialog title={t('notes.trash')} onClose={onClose}>
      {trash.isLoading && <Skeleton className="h-16" />}
      {trash.data?.length === 0 && <Empty>{t('notes.trashEmpty')}</Empty>}
      <ul className="space-y-1">
        {trash.data?.map((n) => (
          <li key={n.id} className="flex items-center gap-2 rounded-lg px-2 py-2 hover:bg-neutral-800/60">
            <span className={cn('h-8 w-1 shrink-0 rounded-full', colorClass(n.color) ?? 'bg-neutral-700')} />
            <div className="min-w-0 flex-1">
              <p className="truncate text-sm">{n.icon} {n.title}</p>
              <p className="text-xs text-neutral-500">
                {n.notes > 1 && <>{t('notes.trashCount').replace('{n}', String(n.notes))} · </>}
                {t('notes.purgeAt').replace('{date}', date.format(new Date(n.purgeAt)))}
              </p>
            </div>
            {canEdit && (
              <Button
                variant="outline"
                size="sm"
                disabled={restore.isPending}
                onClick={() =>
                  restore.mutate(n.id, {
                    onSuccess: () => toast.success(t('notes.restored')),
                    onError: () => toast.error(t('errors.generic')),
                  })
                }
              >
                <RotateCcw />
                {t('notes.restore')}
              </Button>
            )}
          </li>
        ))}
      </ul>
    </Dialog>
  )
}
