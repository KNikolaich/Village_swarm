import type { Plugin } from 'vite'

/**
 * ESP Web Tools (the flashing dialogs) has its texts in English only and no way to translate them. This plugin
 * rewrites its prebuilt bundle (esp-web-tools/dist/web) so every text goes through `__ewt(text, arg)`, which looks
 * the English text up in `globalThis.__ewtDict` (src/lib/espWebToolsRu.ts) and falls back to English.
 *
 * A pattern is the English source where a space matches any whitespace, or none (the bundle keeps the template
 * indentation). A pattern that stops matching after an esp-web-tools update fails the build, so the dialogs never
 * fall back to English silently.
 */

type Rule = [pattern: string, replacement: string]

// Button labels: `<ew-text-button …>Back</ew-text-button>`.
const buttons = ['Back', 'Next', 'Continue', 'Install', 'Erase User Data', 'Reset Device', 'Download Logs', 'Close', 'Connect']

const dialogRules: Rule[] = [
  ['this._error="Disconnected"', 'this._error=__ewt("Disconnected")'],
  ['this._renderProgress("Connecting")', 'this._renderProgress(__ewt("Connecting"))'],
  ['_renderError(e){return["Error",', '_renderError(e){return[__ewt("Error"),'],
  ['<div slot="headline">Connected to ${this._info.name}</div>', '<div slot="headline">${__ewt("Connected to {0}",this._info.name)}</div>'],
  ['`Update ${this._manifest.name}`', '__ewt("Update {0}",this._manifest.name)'],
  ['`Install ${this._manifest.name}`', '__ewt("Install {0}",this._manifest.name)'],
  ['<div slot="headline">Visit Device</div>', '<div slot="headline">${__ewt("Visit Device")}</div>'],
  ['<div slot="headline">Add to Home Assistant</div>', '<div slot="headline">${__ewt("Add to Home Assistant")}</div>'],
  ['"Connect to Wi-Fi":"Change Wi-Fi"', '__ewt("Connect to Wi-Fi"):__ewt("Change Wi-Fi")'],
  ['<div slot="headline">Logs & Console</div>', '<div slot="headline">${__ewt("Logs & Console")}</div>'],
  ['<div slot="headline">Fund Development</div>', '<div slot="headline">${__ewt("Fund Development")}</div>'],
  ['<div slot="headline">Erase User Data</div>', '<div slot="headline">${__ewt("Erase User Data")}</div>'],
  ['i="Configure Wi-Fi"', 'i=__ewt("Configure Wi-Fi")'],
  ['"Scanning for networks":"Trying to connect"', '__ewt("Scanning for networks"):__ewt("Trying to connect")'],
  ['i="Unable to connect"', 'i=__ewt("Unable to connect")'],
  ['i="Timeout"', 'i=__ewt("Timeout")'],
  ['i=`Unknown error (${this._client.error})`', 'i=__ewt("Unknown error ({0})",String(this._client.error))'],
  ['<div>Connect your device to the network to start using it.</div>', '<div>${__ewt("Connect your device to the network to start using it.")}</div>'],
  ['label="Network" @change', 'label=${__ewt("Network")} @change'],
  ['Join other…', '${__ewt("Join other…")}'],
  ['label="Network Name"', 'label=${__ewt("Network Name")}'],
  ['label="Password"', 'label=${__ewt("Password")}'],
  ['"Skip":"Back"', '__ewt("Skip"):__ewt("Back")'],
  ['label="Device connected to the network!"', 'label=${__ewt("Device connected to the network!")}'],
  ['<div slot="headline">Skip</div>', '<div slot="headline">${__ewt("Skip")}</div>'],
  ['_renderAskErase(){return["Erase device",', '_renderAskErase(){return[__ewt("Erase device"),'],
  ['Do you want to erase the device before installing ${this._manifest.name}? All data on the device will be lost.',
    '${__ewt("Do you want to erase the device before installing {0}? All data on the device will be lost.",this._manifest.name)}'],
  ['</ew-checkbox> Erase device </label>', '</ew-checkbox> ${__ewt("Erase device")} </label>'],
  ['e="Erase User Data",t=', 'e=__ewt("Erase User Data"),t='],
  ['Do you want to reset your device and erase all user data from your device?', '${__ewt("Do you want to reset your device and erase all user data from your device?")}'],
  ['e="Installing"', 'e=__ewt("Installing")'],
  ['this._renderProgress("Erasing")', 'this._renderProgress(__ewt("Erasing"))'],
  ['r="Wrapping up"', 'r=__ewt("Wrapping up")'],
  ['r="Installing"', 'r=__ewt("Installing")'],
  ['This will take ${"ESP8266"===this._installState.chipFamily?"a minute":"2 minutes"}.<br /> Keep this page visible to prevent slow down',
    '${__ewt("ESP8266"===this._installState.chipFamily?"This will take a minute.":"This will take 2 minutes.")}<br /> ${__ewt("Keep this page visible to prevent slow down")}'],
  ['label="Installation complete!"', 'label=${__ewt("Installation complete!")}'],
  ['e="Installation failed"', 'e=__ewt("Installation failed")'],
  ['.icon=${"⚠️"} .label=${e}', '.icon=${"⚠️"} .label=${__ewt(e)}'],
  ['.label=${this._installState.message}', '.label=${__ewt(this._installState.message)}'],
  ['this._renderProgress("Preparing installation")', 'this._renderProgress(__ewt("Preparing installation"))'],
  ['e="Confirm Installation"', 'e=__ewt("Confirm Installation")'],
  ['Your device is running ${this._info.firmware}&nbsp;${this._info.version}.',
    '${__ewt("Your device is running {0}.",this._info.firmware+" "+this._info.version)}'],
  ['Do you want to ${r} ${this._manifest.name}&nbsp;${this._manifest.version}?',
    '${__ewt(r==="install"?"Do you want to install {0}?":"Do you want to update to {0}?",this._manifest.name+" "+this._manifest.version)}'],
  ['All data on the device will be erased.`', '${__ewt("All data on the device will be erased.")}`'],
  ['["Logs",e]', '[__ewt("Logs"),e]'],
  ...buttons.map((b): Rule => [`> ${b} </ew-text-button>`, `>\${__ewt(${JSON.stringify(b)})}</ew-text-button>`]),
]

// The "No port selected" dialog (shown when the port picker is closed without a choice).
const noPortRules: Rule[] = [
  ['<div slot="headline">No port selected</div>', '<div slot="headline">${__ewt("No port selected")}</div>'],
  ["If you didn't select a port because you didn't see your device listed, try the following steps:",
    '${__ewt("If you didn\'t select a port because you didn\'t see your device listed, try the following steps:")}'],
  ['Make sure that the device is connected to this computer (the one that runs the browser that shows this website)',
    '${__ewt("Make sure that the device is connected to this computer (the one that runs the browser that shows this website)")}'],
  ['Most devices have a tiny light when it is powered on. If yours has one, make sure it is on.',
    '${__ewt("Most devices have a tiny light when it is powered on. If yours has one, make sure it is on.")}'],
  ['Make sure that the USB cable you use can be used for data and is not a power-only cable.',
    '${__ewt("Make sure that the USB cable you use can be used for data and is not a power-only cable.")}'],
  ['Make sure you have the right drivers installed. Below are the drivers for common chips used in ESP devices:',
    '${__ewt("Make sure you have the right drivers installed. Below are the drivers for common chips used in ESP devices:")}'],
  ['If you are using a Linux flavor, make sure that your user is part of the', '${__ewt("If you are using a Linux flavor, make sure that your user is part of the")}'],
  ['group so it has permission to access the device.', '${__ewt("group so it has permission to access the device.")}'],
  ['You may need to log out & back in or reboot to activate the new group access.', '${__ewt("You may need to log out & back in or reboot to activate the new group access.")}'],
  ['CP2102 drivers:', '${__ewt("CP2102 drivers:")}'],
  ['CH342, CH343, CH9102 drivers:', '${__ewt("CH342, CH343, CH9102 drivers:")}'],
  ['CH340, CH341 drivers:', '${__ewt("CH340, CH341 drivers:")}'],
  ['(download via blue button with ${s} icon)', '${__ewt("(download via blue button with")} ${s} ${__ewt("icon)")}'],
  ...['Close', 'Cancel', 'Try Again'].map((b): Rule => [`> ${b} </ew-text-button>`, `>\${__ewt(${JSON.stringify(b)})}</ew-text-button>`]),
]

const helper =
  'const __ewt=(s,a)=>{const d=globalThis.__ewtDict;const r=(d&&d[s])||s;return a===undefined?r:r.replace("{0}",a)};\n'

const escape = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
const toRegExp = (pattern: string) => new RegExp(pattern.trim().split(/\s+/).map(escape).join('\\s*'), 'g')

function rewrite(code: string, rules: Rule[], file: string) {
  let out = code
  for (const [pattern, replacement] of rules) {
    const re = toRegExp(pattern)
    if (!re.test(out)) throw new Error(`esp-web-tools-i18n: "${pattern}" not found in ${file}; update vite-plugins/esp-web-tools-i18n.ts`)
    out = out.replace(re, () => replacement)
  }
  return helper + out
}

export function espWebToolsI18n(): Plugin {
  return {
    name: 'esp-web-tools-i18n',
    enforce: 'pre',
    transform(code, id) {
      const file = id.split('?')[0].replace(/\\/g, '/')
      if (!file.includes('/esp-web-tools/dist/web/')) return null
      if (/\/install-dialog-[^/]+\.js$/.test(file)) return { code: rewrite(code, dialogRules, file), map: null }
      if (code.includes('No port selected')) return { code: rewrite(code, noPortRules, file), map: null }
      return null
    },
  }
}
