import { type Align, type ClockKind, type Screen } from '@/lib/display'

export interface Row {
  /** The device whose reading is shown; '' is this hornet. */
  device: string
  metric: string
  label: string
  x: number
  y: number
  size: number
  align: Align
  /** A caption the person typed (no source): its text; null for a reading. */
  text: string | null
  /** Font family id ('' is the standard one) and the size in pixels (null: by `size`), for screens the hive draws. */
  font: string
  px: number | null
  bold: boolean
  italic: boolean
  /** The hive's clock instead of a reading (then device and metric are empty), written as `format` says. */
  clock: ClockKind | null
  format: string
  /** `metric` is a state of the device (a relay, a switch), shown as words or, where the hive draws, as two pictures (ids). */
  state: boolean
  onText: string
  offText: string
  onImage: string | null
  offImage: string | null
}

export const noStyle = {
  text: null, font: '', px: null, bold: false, italic: false, clock: null, format: '',
  state: false, onText: '', offText: '', onImage: null, offImage: null,
}

/** What the picker calls a state of a device: `state:relay`, apart from the readings. */
export const statePrefix = 'state:'

/** The tab of a panel with tabs that shows the rows (contracts/monitor-http.md, tabs). */
export const measureTab = 1

/** The id of the clock among the sources of the picker. */
export const clockSource = 'clock'

/** A board the hive does not know the screen of: the 0.42" OLED, the only one so far. */
export const fallbackScreen: Screen = { width: 72, height: 40 }
