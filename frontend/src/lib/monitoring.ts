import type { DeviceDto } from '@/api/client'

/** What the server accepts (Hive.Modules.Telemetry.MonitoringService Hours and Intervals). */
export const monitorHours = [0.5, 1, 2, 3, 4, 6, 8, 12]
export const monitorIntervals = [5, 10, 15, 30]
const monitorTypes = ['meteo', 'heat', 'ble']

/** Our own thermometers that stay on line (no sleep between readings) can be monitored. */
export const canMonitor = (d: DeviceDto) =>
  d.driver === 'hornet' && monitorTypes.includes(d.type) && !d.sleepS && d.status !== 'disabled'
