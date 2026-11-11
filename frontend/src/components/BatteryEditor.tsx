import { Input, Select } from '@/components/ui/primitives'
import { Button } from '@/components/ui/button'
import { HelpTip } from '@/components/HelpTip'
import { FieldLabel } from '@/components/DisplayPreview'
import { clampInt } from '@/lib/displayBox'
import { t } from '@/i18n'
import { batteryBase, batteryBox, type BatteryState, type Screen } from '@/lib/display'

/** The battery icon: where it stands and how big (the hive draws it over the picture, in steps of five percent). */
export function BatteryEditor({ battery, screen, editable, onChange }: { battery: BatteryState; screen: Screen; editable: boolean; onChange: (b: BatteryState) => void }) {
  return (
    <div className="space-y-2 rounded-lg border border-neutral-800 p-2">
      <div className="flex flex-wrap items-end gap-x-4 gap-y-2">
        <label className="flex h-9 items-center gap-2 text-xs text-neutral-300">
          <input type="checkbox" checked={battery.enabled} disabled={!editable} onChange={(e) => onChange({ ...battery, enabled: e.target.checked })} />
          {t('display.battery')}
          <HelpTip text={t('display.batteryHelp')} />
        </label>
        {battery.enabled && (<>
          <label className="space-y-1">
            <FieldLabel text={t('display.batteryScale')} help={t('display.batteryScaleHelp')} />
            <Select value={battery.scale} disabled={!editable} onChange={(e) => onChange({ ...battery, scale: Number(e.target.value) })}>
              {Array.from({ length: batteryBase.maxScale }, (_, k) => k + 1).map((k) => (
                <option key={k} value={k}>{`×${k} (${batteryBase.width * k}×${batteryBase.height * k})`}</option>
              ))}
            </Select>
          </label>
          <label className="space-y-1">
            <FieldLabel text="X" help={t('display.batteryXHelp')} />
            <Input className="h-9 w-20 text-sm" type="number" value={batteryBox(battery, screen).left} disabled={!editable}
              onChange={(e) => onChange({ ...battery, x: clampInt(e.target.value, -100, 400), y: battery.y ?? batteryBox(battery, screen).top })} />
          </label>
          <label className="space-y-1">
            <FieldLabel text="Y" help={t('display.batteryXHelp')} />
            <Input className="h-9 w-20 text-sm" type="number" value={batteryBox(battery, screen).top} disabled={!editable}
              onChange={(e) => onChange({ ...battery, y: clampInt(e.target.value, -100, 400), x: battery.x ?? batteryBox(battery, screen).left })} />
          </label>
          <Button variant="outline" size="sm" disabled={!editable || (battery.x === null && battery.y === null)} onClick={() => onChange({ ...battery, x: null, y: null })}>
            {t('display.batteryCorner')}
          </Button>
        </>)}
      </div>
      {battery.enabled && <p className="text-xs text-neutral-500">{t('display.batteryDrag')}</p>}
    </div>
  )
}
