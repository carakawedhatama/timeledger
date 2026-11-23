import { reactive } from 'vue'
import type { Me } from './api/types'

// Per tab and gone when it closes. Readable by script, hence the CSP in nginx.conf.
const STORAGE_KEY = 'timeledger.token'

export const session = reactive({
  token: sessionStorage.getItem(STORAGE_KEY),
  user: null as Me | null,
})

export function startSession(token: string, user: Me) {
  sessionStorage.setItem(STORAGE_KEY, token)
  session.token = token
  session.user = user
}

export function clearSession() {
  sessionStorage.removeItem(STORAGE_KEY)
  session.token = null
  session.user = null
}

// These only decide what to show. The API enforces the same rules whatever the UI does.
export const isAdmin = () => session.user?.role === 'PayrollAdmin'
export const isSelf = (employeeId: string) => session.user?.employeeId === employeeId
export const canActFor = (employeeId: string) => isAdmin() || isSelf(employeeId)
export const canDecideLeave = (employeeId: string) =>
  !isSelf(employeeId) && (isAdmin() || session.user?.role === 'Supervisor')
