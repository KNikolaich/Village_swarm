/**
 * The desktop sidebar: full rows with labels, or a narrow bar of icons.
 * On a wide screen the user's choice is kept in this browser; on a tablet-wide one the bar starts narrow,
 * and opening it there lasts until the screen changes width (it is not saved).
 */
const storageKey = 'shell.sidebarCollapsed'

/** The choice kept for wide screens; open when there is none or storage is closed (private window). */
export function loadSidebarCollapsed(storage: Pick<Storage, 'getItem'> | undefined = globalThis.localStorage): boolean {
  try {
    return storage?.getItem(storageKey) === '1'
  } catch {
    return false
  }
}

export function saveSidebarCollapsed(collapsed: boolean, storage: Pick<Storage, 'setItem'> | undefined = globalThis.localStorage) {
  try {
    storage?.setItem(storageKey, collapsed ? '1' : '0')
  } catch {
    // private window or storage full: just not remembered
  }
}

/** Whether the bar is narrow now: the saved choice on a wide screen, narrow on a tablet unless opened there. */
export function sidebarCollapsed(wide: boolean, saved: boolean, openedOnTablet: boolean): boolean {
  return wide ? saved : !openedOnTablet
}
