<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api } from '../api/client'
import type { DayKind, EmployeeTimesheet, PayPeriod, PeriodTimesheets } from '../api/types'
import { isAdmin } from '../auth'
import { formatDate, formatDay, formatIdr, formatMinutes, todayIso } from '../format'
import { attempt } from '../notify'

const periods = ref<PayPeriod[]>([])
const selectedId = ref<string | null>(null)
const data = ref<PeriodTimesheets | null>(null)
const loading = ref(false)
const closing = ref(false)
const confirmClose = ref(false)
const expanded = ref<string | null>(null)

const selected = computed(() => periods.value.find((p) => p.id === selectedId.value) ?? null)
const canClose = computed(() => selected.value?.status === 'Open' && selected.value.endDate < todayIso())

const totals = computed(() => {
  const rows = data.value?.employees ?? []
  return {
    overtimeMinutes: rows.reduce((s, e) => s + e.summary.overtimeMinutes, 0),
    overtimePay: rows.reduce((s, e) => s + (e.summary.overtimePay ?? 0), 0),
    anomalies: rows.reduce((s, e) => s + e.summary.anomalyCount, 0),
  }
})

const kindLabel: Record<DayKind, string> = { Workday: 'Workday', RestDay: 'Rest day', PublicHoliday: 'Holiday' }

function leaveSummary(e: EmployeeTimesheet) {
  const parts = [
    e.summary.annualLeaveDays && `${e.summary.annualLeaveDays} annual`,
    e.summary.sickLeaveDays && `${e.summary.sickLeaveDays} sick`,
    e.summary.unpaidLeaveDays && `${e.summary.unpaidLeaveDays} unpaid`,
  ].filter(Boolean)
  return parts.length ? parts.join(', ') : '—'
}

async function loadPeriods() {
  const result = await attempt(() => api.payPeriods())
  if (!result) return
  periods.value = result
  selectedId.value ??= result.find((p) => p.status === 'Open' && p.endDate < todayIso())?.id ?? result[0]?.id ?? null
}

async function loadTimesheets() {
  if (!selectedId.value) return
  loading.value = true
  data.value = (await attempt(() => api.timesheets(selectedId.value!))) ?? null
  loading.value = false
}

async function closePeriod() {
  confirmClose.value = false
  if (!selectedId.value) return
  closing.value = true
  const result = await attempt(() => api.closePeriod(selectedId.value!), 'Period closed. Payroll export queued for delivery.')
  closing.value = false
  if (result) {
    data.value = result
    await loadPeriods()
  }
}

watch(selectedId, loadTimesheets)
onMounted(loadPeriods)
</script>

<template>
  <div class="d-flex align-center flex-wrap ga-4 mb-4">
    <v-select
      v-model="selectedId"
      :items="periods"
      item-value="id"
      :item-title="(p: PayPeriod) => `${formatDate(p.startDate)} – ${formatDate(p.endDate)} (${p.status})`"
      label="Pay period"
      density="comfortable"
      hide-details
      style="max-width: 420px"
    />
    <v-chip v-if="data" :color="data.isFinal ? 'secondary' : 'warning'" variant="tonal">
      {{ data.isFinal ? 'Final — frozen at close' : 'Live preview' }}
    </v-chip>
    <v-spacer />
    <v-btn v-if="isAdmin()" color="primary" :disabled="!canClose" :loading="closing" prepend-icon="mdi-lock-check-outline" @click="confirmClose = true">
      Close period &amp; export
    </v-btn>
  </div>

  <v-row v-if="data" class="mb-2">
    <v-col cols="12" sm="4">
      <v-card border flat class="pa-4">
        <div class="text-overline">Overtime hours</div>
        <div class="text-h5 font-weight-bold">{{ formatMinutes(totals.overtimeMinutes) }}</div>
      </v-card>
    </v-col>
    <v-col cols="12" sm="4">
      <v-card border flat class="pa-4">
        <div class="text-overline">{{ isAdmin() ? 'Statutory overtime pay' : 'Your overtime pay' }}</div>
        <div class="text-h5 font-weight-bold">{{ formatIdr(totals.overtimePay) }}</div>
      </v-card>
    </v-col>
    <v-col cols="12" sm="4">
      <v-card border flat class="pa-4">
        <div class="text-overline">Anomalies to review</div>
        <div class="text-h5 font-weight-bold" :class="totals.anomalies ? 'text-error' : ''">{{ totals.anomalies }}</div>
      </v-card>
    </v-col>
  </v-row>

  <v-card border flat>
    <v-progress-linear v-if="loading" indeterminate />
    <v-table density="comfortable">
      <thead>
        <tr>
          <th></th>
          <th>Employee</th>
          <th class="text-right">Days worked</th>
          <th class="text-right">Regular</th>
          <th class="text-right">Overtime</th>
          <th class="text-right">OT pay (PP 35/2021)</th>
          <th>Leave</th>
          <th class="text-right">Anomalies</th>
        </tr>
      </thead>
      <tbody>
        <template v-for="e in data?.employees ?? []" :key="e.employeeId">
          <tr class="cursor-pointer" @click="expanded = expanded === e.employeeId ? null : e.employeeId">
            <td><v-icon size="small" :icon="expanded === e.employeeId ? 'mdi-chevron-down' : 'mdi-chevron-right'" /></td>
            <td>
              <div>{{ e.fullName }}</div>
              <div class="text-caption text-medium-emphasis">{{ e.employeeCode }}</div>
            </td>
            <td class="text-right">{{ e.summary.daysWorked }}</td>
            <td class="text-right">{{ formatMinutes(e.summary.regularMinutes) }}</td>
            <td class="text-right">{{ formatMinutes(e.summary.overtimeMinutes) }}</td>
            <td class="text-right">{{ e.summary.overtimePay === null ? 'Hidden' : formatIdr(e.summary.overtimePay) }}</td>
            <td>{{ leaveSummary(e) }}</td>
            <td class="text-right">
              <v-chip v-if="e.summary.anomalyCount" color="error" size="small" variant="tonal">{{ e.summary.anomalyCount }}</v-chip>
              <span v-else class="text-medium-emphasis">0</span>
            </td>
          </tr>
          <tr v-if="expanded === e.employeeId">
            <td colspan="8" class="bg-grey-lighten-4 pa-0">
              <v-table density="compact" class="bg-transparent">
                <thead>
                  <tr>
                    <th>Date</th>
                    <th>Day</th>
                    <th class="text-right">Worked</th>
                    <th class="text-right">Overtime</th>
                    <th class="text-right">OT pay</th>
                    <th>Leave</th>
                    <th>Notes</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="d in e.days" :key="d.date">
                    <td>{{ formatDay(d.date) }}</td>
                    <td>{{ kindLabel[d.kind] }}</td>
                    <td class="text-right">{{ formatMinutes(d.workedMinutes) }}</td>
                    <td class="text-right">{{ d.overtimeMinutes ? formatMinutes(d.overtimeMinutes) : '—' }}</td>
                    <td class="text-right">{{ d.overtimePay ? formatIdr(d.overtimePay) : '—' }}</td>
                    <td>{{ d.leave ?? '—' }}</td>
                    <td class="text-error text-caption">{{ d.anomalies.join(' · ') }}</td>
                  </tr>
                </tbody>
              </v-table>
            </td>
          </tr>
        </template>
      </tbody>
    </v-table>
  </v-card>

  <v-dialog v-model="confirmClose" max-width="480">
    <v-card title="Close this pay period?">
      <v-card-text>
        Timesheets will be frozen and a <code>payroll.period_closed</code> event will be sent to every subscribed
        payroll integration. Review the {{ totals.anomalies }} anomalies first if any remain.
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn @click="confirmClose = false">Cancel</v-btn>
        <v-btn color="primary" variant="flat" @click="closePeriod">Close &amp; export</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>
