// Web Serial (Chrome/Edge, HTTPS or localhost): talks to the hornet's serial console to configure it
// right after flashing (firmware/README.md "First start"). Minimal typings: the API is not in lib.dom yet.

export type SerialPortLike = {
  open(options: { baudRate: number }): Promise<void>
  close(): Promise<void>
  setSignals(signals: { dataTerminalReady?: boolean; requestToSend?: boolean }): Promise<void>
  readable: ReadableStream<Uint8Array> | null
  writable: WritableStream<Uint8Array> | null
}

type SerialLike = { requestPort(): Promise<SerialPortLike> }

export const serialSupported = () => 'serial' in navigator

export type ConsoleLine = (line: string) => void

/** An open serial console: send lines, read lines, close. */
export class HornetSerial {
  private reader?: ReadableStreamDefaultReader<Uint8Array>
  private buffer = ''
  private closed = false
  private readonly waiters = new Set<ConsoleLine>()

  private constructor(private readonly port: SerialPortLike, private readonly onLine: ConsoleLine) {}

  /** Asks the user to pick the board's port (must be called from a click). */
  static async open(onLine: ConsoleLine): Promise<HornetSerial> {
    return HornetSerial.attach(await (navigator as unknown as { serial: SerialLike }).serial.requestPort(), onLine)
  }

  /** The console on a port already picked (by the setup wizard, right after flashing through it). */
  static async attach(port: SerialPortLike, onLine: ConsoleLine): Promise<HornetSerial> {
    await port.open({ baudRate: 115200 })
    // ESP32-CAM-MB wires DTR/RTS to EN/IO0: release both so the board runs the firmware, not the bootloader.
    await port.setSignals({ dataTerminalReady: false, requestToSend: false })
    const s = new HornetSerial(port, onLine)
    void s.readLoop()
    return s
  }

  /** The first line that matches, or null after the timeout. Lines that came before the call do not count. */
  waitForLine(match: (line: string) => boolean, timeoutMs: number): Promise<string | null> {
    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        this.waiters.delete(waiter)
        resolve(null)
      }, timeoutMs)
      const waiter = (line: string) => {
        if (!match(line)) return
        clearTimeout(timer)
        this.waiters.delete(waiter)
        resolve(line)
      }
      this.waiters.add(waiter)
    })
  }

  async send(line: string) {
    const writer = this.port.writable!.getWriter()
    try {
      await writer.write(new TextEncoder().encode(line + '\n'))
    } finally {
      writer.releaseLock()
    }
  }

  /** Resets the board through the adapter's RTS line (ESP32-CAM-MB and similar boards wire it to EN). */
  async pulseReset() {
    await this.port.setSignals({ dataTerminalReady: false, requestToSend: true })
    await new Promise((resolve) => setTimeout(resolve, 150))
    await this.port.setSignals({ dataTerminalReady: false, requestToSend: false })
  }

  async close() {
    this.closed = true
    try {
      await this.reader?.cancel()
    } catch { /* already closed */ }
    await this.port.close().catch(() => {})
  }

  private async readLoop() {
    const decoder = new TextDecoder()
    while (!this.closed && this.port.readable) {
      this.reader = this.port.readable.getReader()
      try {
        for (;;) {
          const { value, done } = await this.reader.read()
          if (done) break
          this.buffer += decoder.decode(value, { stream: true })
          let nl: number
          while ((nl = this.buffer.indexOf('\n')) >= 0) {
            const line = this.buffer.slice(0, nl).replace(/\r$/, '')
            this.buffer = this.buffer.slice(nl + 1)
            if (!line) continue
            this.onLine(line)
            for (const waiter of [...this.waiters]) waiter(line)
          }
        }
      } catch {
        break
      } finally {
        this.reader.releaseLock()
      }
    }
  }
}
