import { clearSession, session } from '../auth'
import type {
  ClockEvent, ClockEventType, Delivery, DeliveryStatus, Employee, Integration, LeaveRequest, LeaveStatus,
  LeaveType, LoginResult, Me, PayPeriod, PeriodTimesheets, PresenceEntry, ProblemDetails,
} from './types'

export class ApiError extends Error {
  constructor(public readonly status: number, public readonly problem: ProblemDetails) {
    const details = problem.errors ? Object.values(problem.errors).flat().join(' ') : ''
    super([problem.title ?? `Request failed (${status})`, details].filter(Boolean).join(' — '))
  }
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const token = session.token
  const response = await fetch(path, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      Accept: 'application/json',
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...init.headers,
    },
  })

  if (!response.ok) {
    // Only if it's still the same session: a late 401 from an old token must not end a new sign-in.
    if (response.status === 401 && token && token === session.token) clearSession()
    const problem = (await response.json().catch(() => ({}))) as ProblemDetails
    throw new ApiError(response.status, problem)
  }

  return response.status === 202 || response.status === 204 ? (undefined as T) : ((await response.json()) as T)
}

const post = <T>(path: string, body?: unknown, headers?: Record<string, string>) =>
  request<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body), headers })

export const api = {
  login: (email: string, password: string) => post<LoginResult>('/api/auth/login', { email, password }),
  me: () => request<Me>('/api/auth/me'),
  logout: () => post<void>('/api/auth/logout'),
  changePassword: (currentPassword: string, newPassword: string) =>
    post<void>('/api/auth/change-password', { currentPassword, newPassword }),

  employees: () => request<Employee[]>('/api/employees'),
  presence: () => request<PresenceEntry[]>('/api/presence'),

  /** A fresh key per user action; retrying the same action (e.g. after a timeout) reuses it. */
  punch: (employeeId: string, type: ClockEventType, idempotencyKey: string = crypto.randomUUID()) =>
    post<ClockEvent>('/api/clock-events', { employeeId, type, source: 'Web' }, { 'Idempotency-Key': idempotencyKey }),

  payPeriods: () => request<PayPeriod[]>('/api/pay-periods'),
  timesheets: (periodId: string) => request<PeriodTimesheets>(`/api/pay-periods/${periodId}/timesheets`),
  closePeriod: (periodId: string) => post<PeriodTimesheets>(`/api/pay-periods/${periodId}/close`),

  leaveRequests: (status?: LeaveStatus) =>
    request<LeaveRequest[]>(`/api/leave-requests${status ? `?status=${status}` : ''}`),
  requestLeave: (body: { employeeId: string; type: LeaveType; startDate: string; endDate: string; reason?: string }) =>
    post<LeaveRequest>('/api/leave-requests', body),
  approveLeave: (id: string) => post<LeaveRequest>(`/api/leave-requests/${id}/approve`, {}),
  rejectLeave: (id: string, note?: string) => post<LeaveRequest>(`/api/leave-requests/${id}/reject`, { note }),

  integrations: () => request<Integration[]>('/api/integrations'),
  deliveries: (status?: DeliveryStatus) => request<Delivery[]>(`/api/deliveries${status ? `?status=${status}` : ''}`),
  retryDelivery: (id: string) => post<void>(`/api/deliveries/${id}/retry`),
}
