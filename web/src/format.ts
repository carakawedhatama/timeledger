const idr = new Intl.NumberFormat('id-ID', { style: 'currency', currency: 'IDR', maximumFractionDigits: 0 })
const dateFmt = new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' })
const dayFmt = new Intl.DateTimeFormat('en-GB', { weekday: 'short', day: 'numeric', month: 'short', timeZone: 'UTC' })
const timeFmt = new Intl.DateTimeFormat('en-GB', { hour: '2-digit', minute: '2-digit', timeZone: 'Asia/Jakarta' })
const dateTimeFmt = new Intl.DateTimeFormat('en-GB', {
  day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', second: '2-digit', timeZone: 'Asia/Jakarta',
})

export const formatIdr = (value: number) => idr.format(value)

/** 485 → "8h 05m" */
export const formatMinutes = (minutes: number) => `${Math.floor(minutes / 60)}h ${String(minutes % 60).padStart(2, '0')}m`

/** "2026-11-02" (a DateOnly) → "2 Nov 2026"; parsed as UTC so the day never shifts. */
export const formatDate = (isoDate: string) => dateFmt.format(new Date(`${isoDate}T00:00:00Z`))
export const formatDay = (isoDate: string) => dayFmt.format(new Date(`${isoDate}T00:00:00Z`))

/** Instants are shown in Jakarta time (WIB). */
export const formatTime = (instant: string) => timeFmt.format(new Date(instant))
export const formatDateTime = (instant: string) => dateTimeFmt.format(new Date(instant))

export const todayIso = () => new Date().toLocaleDateString('en-CA', { timeZone: 'Asia/Jakarta' })
