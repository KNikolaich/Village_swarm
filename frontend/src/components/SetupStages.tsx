import { SetupError, type SetupStage } from '@/lib/hornetSetup'
import { t } from '@/i18n'

/** The stages of a setup over USB with what became of each: done, now, failed or still to come. */
export function SetupStages({ stages, stage, running, failed, progress, names = 'add.stage' }: {
  stages: SetupStage[]
  stage: SetupStage
  running: boolean
  failed: SetupError | null
  progress: number
  names?: string
}) {
  const current = stages.indexOf(stage)
  return (
    <ol className="space-y-1 text-xs">
      {stages.map((s, index) => {
        const state = failed && s === failed.stage ? 'failed' : index < current || (s === stage && !running && !failed) ? 'done' : s === stage ? 'now' : 'later'
        return (
          <li key={s} className={state === 'failed' ? 'text-red-400' : state === 'done' ? 'text-emerald-400' : state === 'now' ? 'text-amber-300' : 'text-neutral-500'}>
            {state === 'done' ? '✓' : state === 'failed' ? '✗' : state === 'now' ? '…' : '○'} {t(`${names}.${s}`)}
            {s === 'flash' && state === 'now' && ` ${progress}%`}
          </li>
        )
      })}
    </ol>
  )
}
