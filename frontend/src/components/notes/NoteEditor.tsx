import {
  BlockTypeSelect,
  BoldItalicUnderlineToggles,
  CodeToggle,
  CreateLink,
  InsertImage,
  InsertTable,
  InsertThematicBreak,
  ListsToggle,
  MDXEditor,
  Separator,
  UndoRedo,
  codeBlockPlugin,
  headingsPlugin,
  imagePlugin,
  linkDialogPlugin,
  linkPlugin,
  listsPlugin,
  markdownShortcutPlugin,
  quotePlugin,
  tablePlugin,
  thematicBreakPlugin,
  toolbarPlugin,
  type MDXEditorMethods,
} from '@mdxeditor/editor'
import '@mdxeditor/editor/style.css'
import { useQueryClient } from '@tanstack/react-query'
import { Ban, Check, ChevronLeft, Loader2, Paperclip, Trash2, X } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { toast } from 'sonner'
import { ApiError, type NoteDto } from '@/api/client'
import { noteKeys, uploadNoteAttachment, useDeleteNoteAttachment, useUpdateNote } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { getLanguage, t } from '@/i18n'
import { formatWhen } from '@/lib/format'
import { NOTE_COLORS, NOTE_ICONS, colorClass } from '@/lib/notes'
import { cn } from '@/lib/utils'

type SaveState = 'saved' | 'saving' | 'error'

const SAVE_DELAY_MS = 800

/** The editor speaks the hive's language: its own strings come from i18n (notes.mdx.*), English keeps its defaults. */
function translate(key: string, defaultValue: string, interpolations?: Record<string, unknown>) {
  let text = getLanguage() === 'en' ? defaultValue : t(`notes.mdx.${key}`, defaultValue)
  for (const [name, value] of Object.entries(interpolations ?? {})) text = text.replaceAll(`{{${name}}}`, String(value ?? ''))
  return text
}

/**
 * One note: emoji, bookmark color and title in the header, the Markdown text below. Everything saves by itself a
 * moment after typing stops. Pictures paste from the clipboard, drop in, or come from a file; other files attach.
 */
export function NoteEditor({ note, canEdit, onDelete }: { note: NoteDto; canEdit: boolean; onDelete: () => void }) {
  const qc = useQueryClient()
  const update = useUpdateNote()
  const removeAttachment = useDeleteNoteAttachment(note.id)
  const editor = useRef<MDXEditorMethods>(null)
  const fileInput = useRef<HTMLInputElement>(null)
  const [title, setTitle] = useState(note.title)
  const [save, setSave] = useState<SaveState>('saved')
  const [uploading, setUploading] = useState(0)
  const [picker, setPicker] = useState(false)
  const pending = useRef<{ title?: string; body?: string }>({})
  const timer = useRef<number | undefined>(undefined)

  const flush = useCallback(() => {
    window.clearTimeout(timer.current)
    const changes = pending.current
    pending.current = {}
    if (changes.title === undefined && changes.body === undefined) return
    setSave('saving')
    update.mutate(
      { id: note.id, ...changes },
      {
        onSuccess: () => setSave((s) => (s === 'saving' ? 'saved' : s)),
        onError: () => {
          // Keep what did not reach the server, so the next keystroke or leaving the note tries again.
          pending.current = { ...changes, ...pending.current }
          setSave('error')
        },
      },
    )
  }, [note.id, update])

  const schedule = useCallback(
    (changes: { title?: string; body?: string }) => {
      pending.current = { ...pending.current, ...changes }
      setSave('saving')
      window.clearTimeout(timer.current)
      timer.current = window.setTimeout(flush, SAVE_DELAY_MS)
    },
    [flush],
  )

  // Leaving the note (another note, another screen, closing the tab) saves what is still waiting.
  const flushRef = useRef(flush)
  flushRef.current = flush
  useEffect(() => {
    const beforeUnload = () => flushRef.current()
    window.addEventListener('beforeunload', beforeUnload)
    return () => {
      window.removeEventListener('beforeunload', beforeUnload)
      flushRef.current()
    }
  }, [])

  const saveHeader = (changes: { icon?: string; color?: string }) =>
    update.mutate({ id: note.id, ...changes }, { onError: () => toast.error(t('notes.saveFailed')) })

  const upload = useCallback(
    async (file: File) => {
      setUploading((n) => n + 1)
      try {
        const attachment = await uploadNoteAttachment(note.id, file)
        void qc.invalidateQueries({ queryKey: noteKeys.note(note.id) })
        return attachment
      } catch (e) {
        toast.error(e instanceof ApiError && e.status === 413 ? t('notes.tooLarge') : t('notes.uploadFailed'))
        throw e
      } finally {
        setUploading((n) => n - 1)
      }
    },
    [note.id, qc],
  )

  async function attachFiles(files: FileList | null) {
    for (const file of Array.from(files ?? [])) {
      try {
        const attachment = await upload(file)
        editor.current?.insertMarkdown(attachment.markdown)
      } catch {
        // Already reported.
      }
    }
    if (fileInput.current) fileInput.current.value = ''
  }

  const bookmark = colorClass(note.color)

  return (
    <article className="flex min-h-0 flex-col gap-3">
      <div className="flex items-center gap-2 md:hidden">
        <Link to="/notes" className="inline-flex items-center gap-1 text-sm text-neutral-400">
          <ChevronLeft className="size-4" />
          {t('notes.back')}
        </Link>
      </div>

      {note.path.length > 0 && (
        <nav className="flex flex-wrap items-center gap-1 text-xs text-neutral-500">
          {note.path.map((c, i) => (
            <span key={c.id} className="flex items-center gap-1">
              {i > 0 && <span>/</span>}
              <Link to={`/notes/${c.id}`} className="hover:text-neutral-300">{c.icon} {c.title}</Link>
            </span>
          ))}
        </nav>
      )}

      <header className="flex items-start gap-2">
        <span className={cn('mt-1 h-9 w-1.5 shrink-0 rounded-full', bookmark ?? 'bg-neutral-800')} />
        <div className="relative">
          <button
            type="button"
            disabled={!canEdit}
            onClick={() => setPicker((p) => !p)}
            className="flex size-11 items-center justify-center rounded-lg border border-neutral-800 text-2xl hover:border-neutral-600 disabled:hover:border-neutral-800"
            aria-label={t('notes.icon')}
            title={t('notes.icon')}
          >
            {note.icon ?? <span className="text-base text-neutral-600">☺</span>}
          </button>
          {picker && (
            <HeaderPicker
              icon={note.icon}
              color={note.color}
              onIcon={(icon) => saveHeader({ icon })}
              onColor={(color) => saveHeader({ color })}
              onClose={() => setPicker(false)}
            />
          )}
        </div>
        <input
          value={title}
          readOnly={!canEdit}
          maxLength={200}
          onChange={(e) => {
            setTitle(e.target.value)
            if (e.target.value.trim()) schedule({ title: e.target.value.trim() })
          }}
          onBlur={flush}
          className="min-w-0 flex-1 bg-transparent py-1 text-xl font-semibold text-neutral-50 outline-none placeholder:text-neutral-600"
          placeholder={t('notes.untitled')}
          aria-label={t('notes.title')}
        />
        {canEdit && (
          <Button variant="ghost" size="icon" onClick={onDelete} aria-label={t('notes.delete')} title={t('notes.delete')}>
            <Trash2 />
          </Button>
        )}
      </header>

      <div className="flex flex-wrap items-center gap-3 text-xs text-neutral-500">
        <span>{t('notes.updated').replace('{date}', formatWhen(note.updatedAt))}</span>
        {canEdit ? <SaveBadge state={save} uploading={uploading > 0} /> : <span>{t('notes.readOnly')}</span>}
        {canEdit && (
          <>
            <Button variant="ghost" size="sm" className="ml-auto" onClick={() => fileInput.current?.click()}>
              <Paperclip />
              {t('notes.attach')}
            </Button>
            <input ref={fileInput} type="file" multiple hidden onChange={(e) => void attachFiles(e.target.files)} />
          </>
        )}
      </div>

      <div className="note-editor rounded-xl border border-neutral-800 bg-neutral-900/70">
        <MDXEditor
          key={note.id}
          ref={editor}
          markdown={note.body}
          readOnly={!canEdit}
          className="dark-theme"
          contentEditableClassName="note-content"
          placeholder={t('notes.placeholder')}
          translation={translate}
          onChange={(markdown, initialNormalize) => !initialNormalize && schedule({ body: markdown })}
          onBlur={flush}
          plugins={[
            headingsPlugin(),
            listsPlugin(),
            quotePlugin(),
            thematicBreakPlugin(),
            linkPlugin(),
            linkDialogPlugin(),
            tablePlugin(),
            codeBlockPlugin({ defaultCodeBlockLanguage: '' }),
            imagePlugin({ imageUploadHandler: async (file) => (await upload(file)).url, disableImageSettingsButton: true }),
            markdownShortcutPlugin(),
            ...(canEdit
              ? [
                  toolbarPlugin({
                    toolbarContents: () => (
                      <>
                        <UndoRedo />
                        <Separator />
                        <BlockTypeSelect />
                        <BoldItalicUnderlineToggles />
                        <CodeToggle />
                        <Separator />
                        <ListsToggle />
                        <Separator />
                        <CreateLink />
                        <InsertImage />
                        <InsertTable />
                        <InsertThematicBreak />
                      </>
                    ),
                  }),
                ]
              : []),
          ]}
        />
      </div>

      {note.attachments.length > 0 && (
        <section className="space-y-1">
          <h3 className="text-xs font-medium uppercase tracking-wide text-neutral-500">{t('notes.attachments')}</h3>
          <ul className="flex flex-wrap gap-2">
            {note.attachments.map((a) => (
              <li key={a.id} className="flex items-center gap-2 rounded-lg border border-neutral-800 bg-neutral-900/70 py-1 pl-1 pr-1 text-sm">
                {a.isImage ? (
                  <a href={a.url} target="_blank" rel="noreferrer"><img src={a.url} alt="" className="size-9 rounded object-cover" /></a>
                ) : (
                  <Paperclip className="ml-1 size-4 text-neutral-500" />
                )}
                <a href={a.url} target="_blank" rel="noreferrer" className="max-w-48 truncate hover:text-amber-300">{a.fileName}</a>
                <span className="text-xs text-neutral-500">{formatBytes(a.bytes)}</span>
                {canEdit && (
                  <button
                    type="button"
                    onClick={() => removeAttachment.mutate(a.id)}
                    className="rounded p-1 text-neutral-500 hover:text-red-400"
                    aria-label={t('notes.removeAttachment')}
                    title={t('notes.removeAttachment')}
                  >
                    <X className="size-3.5" />
                  </button>
                )}
              </li>
            ))}
          </ul>
        </section>
      )}
    </article>
  )
}

function SaveBadge({ state, uploading }: { state: SaveState; uploading: boolean }) {
  if (uploading) return <span className="flex items-center gap-1"><Loader2 className="size-3 animate-spin" />{t('notes.uploading')}</span>
  if (state === 'saving') return <span className="flex items-center gap-1"><Loader2 className="size-3 animate-spin" />{t('notes.saving')}</span>
  if (state === 'error') return <span className="text-red-400">{t('notes.saveFailed')}</span>
  return <span className="flex items-center gap-1 text-emerald-400/80"><Check className="size-3" />{t('notes.saved')}</span>
}

function HeaderPicker({ icon, color, onIcon, onColor, onClose }: {
  icon: string | null
  color: string | null
  onIcon: (icon: string) => void
  onColor: (color: string) => void
  onClose: () => void
}) {
  const [custom, setCustom] = useState('')
  return (
    <>
      <div className="fixed inset-0 z-30" onClick={onClose} />
      <div className="absolute left-0 z-40 mt-2 w-72 space-y-3 rounded-xl border border-neutral-700 bg-neutral-900 p-3 shadow-xl">
        <div>
          <p className="mb-1 text-xs text-neutral-500">{t('notes.icon')}</p>
          <div className="grid grid-cols-8 gap-1">
            {NOTE_ICONS.map((e) => (
              <button
                key={e}
                type="button"
                onClick={() => onIcon(e)}
                className={cn('flex size-8 items-center justify-center rounded text-lg hover:bg-neutral-800', icon === e && 'bg-neutral-800 ring-1 ring-amber-400')}
              >
                {e}
              </button>
            ))}
          </div>
          <div className="mt-2 flex gap-1">
            <input
              value={custom}
              onChange={(e) => setCustom(e.target.value)}
              maxLength={16}
              placeholder={t('notes.iconCustom')}
              className="h-8 min-w-0 flex-1 rounded border border-neutral-700 bg-neutral-950 px-2 text-sm outline-none focus:border-amber-400"
            />
            <Button size="sm" variant="secondary" disabled={!custom.trim()} onClick={() => onIcon(custom.trim())}><Check /></Button>
            <Button size="sm" variant="ghost" onClick={() => onIcon('')} title={t('notes.noIcon')} aria-label={t('notes.noIcon')}><Ban /></Button>
          </div>
        </div>
        <div>
          <p className="mb-1 text-xs text-neutral-500">{t('notes.color')}</p>
          <div className="flex flex-wrap gap-1.5">
            {NOTE_COLORS.map((c) => (
              <button
                key={c}
                type="button"
                onClick={() => onColor(c)}
                title={t(`notes.colors.${c}`)}
                aria-label={t(`notes.colors.${c}`)}
                className={cn('size-7 rounded-full', colorClass(c), color === c && 'ring-2 ring-white ring-offset-2 ring-offset-neutral-900')}
              />
            ))}
            <button
              type="button"
              onClick={() => onColor('')}
              title={t('notes.noColor')}
              aria-label={t('notes.noColor')}
              className={cn('flex size-7 items-center justify-center rounded-full border border-neutral-600 text-neutral-500', !color && 'ring-2 ring-white ring-offset-2 ring-offset-neutral-900')}
            >
              <Ban className="size-3.5" />
            </button>
          </div>
        </div>
      </div>
    </>
  )
}

function formatBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}
