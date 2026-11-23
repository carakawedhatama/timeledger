// Mirrors TimeLedger.Api/Contracts/Dtos.cs. Enums are serialized as strings.

export type UserRole = 'Employee' | 'Supervisor' | 'PayrollAdmin'
export type WorkSchedule = 'FiveDay' | 'SixDay'
export type ClockEventType = 'ClockIn' | 'ClockOut'
export type ClockSource = 'Web' | 'Mobile' | 'Kiosk' | 'Api'
export type DayKind = 'Workday' | 'RestDay' | 'PublicHoliday'
export type LeaveType = 'Annual' | 'Sick' | 'Unpaid'
export type LeaveStatus = 'Pending' | 'Approved' | 'Rejected'
export type PayPeriodStatus = 'Open' | 'Closed'
export type IntegrationProvider = 'GenericWebhook' | 'GustoStyle'
export type DeliveryStatus = 'Pending' | 'Succeeded' | 'Skipped' | 'DeadLettered'

export interface Me {
  id: string
  email: string
  role: UserRole
  employeeId: string | null
  fullName: string | null
}

export interface LoginResult {
  accessToken: string
  tokenType: 'Bearer'
  expiresAt: string
  user: Me
}

export interface Employee {
  id: string
  employeeCode: string
  fullName: string
  email: string
  monthlyBaseSalary: number | null
  schedule: WorkSchedule
  timeZoneId: string
  supervisorId: string | null
  isActive: boolean
}

export interface PresenceEntry {
  employeeId: string
  employeeCode: string
  fullName: string
  status: 'in' | 'out'
  since: string | null
}

export interface ClockEvent {
  id: string
  employeeId: string
  type: ClockEventType
  occurredAt: string
  source: ClockSource
}

export interface LeaveRequest {
  id: string
  employeeId: string
  employeeCode: string
  employeeName: string
  type: LeaveType
  startDate: string
  endDate: string
  days: number
  status: LeaveStatus
  reason: string | null
  decidedBy: string | null
  decisionNote: string | null
  decidedAt: string | null
  createdAt: string
}

export interface PayPeriod {
  id: string
  startDate: string
  endDate: string
  status: PayPeriodStatus
  closedAt: string | null
}

export interface DailyTimesheet {
  date: string
  kind: DayKind
  workedMinutes: number
  regularMinutes: number
  overtimeMinutes: number
  overtimePay: number | null
  leave: LeaveType | null
  anomalies: string[]
}

export interface TimesheetSummary {
  workedMinutes: number
  regularMinutes: number
  overtimeMinutes: number
  overtimePay: number | null
  daysWorked: number
  annualLeaveDays: number
  sickLeaveDays: number
  unpaidLeaveDays: number
  anomalyCount: number
}

export interface EmployeeTimesheet {
  employeeId: string
  employeeCode: string
  fullName: string
  summary: TimesheetSummary
  days: DailyTimesheet[]
}

export interface PeriodTimesheets {
  period: PayPeriod
  isFinal: boolean
  employees: EmployeeTimesheet[]
}

export interface Integration {
  id: string
  name: string
  provider: IntegrationProvider
  endpointUrl: string
  eventTypes: string[]
  isEnabled: boolean
  secretHint: string
}

export interface Delivery {
  id: string
  connectionId: string
  connectionName: string
  eventType: string
  status: DeliveryStatus
  attempts: number
  nextAttemptAt: string
  lastStatusCode: number | null
  lastError: string | null
  createdAt: string
  completedAt: string | null
}

export interface ProblemDetails {
  title?: string
  status?: number
  errors?: Record<string, string[]>
}
