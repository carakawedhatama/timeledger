<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { api } from '../api/client'
import type { Employee, LeaveRequest, LeaveStatus, LeaveType } from '../api/types'
import { canDecideLeave, isAdmin, session } from '../auth'
import { formatDate } from '../format'
import { attempt } from '../notify'

const tab = ref<LeaveStatus | 'All'>('Pending')
const requests = ref<LeaveRequest[]>([])
const employees = ref<Employee[]>([])
const loading = ref(false)
const dialog = ref(false)
const form = ref({ employeeId: session.user?.employeeId ?? '', type: 'Annual' as LeaveType, startDate: '', endDate: '', reason: '' })

const statusColor: Record<LeaveStatus, string> = { Pending: 'warning', Approved: 'success', Rejected: 'error' }

async function load() {
  loading.value = true
  requests.value = (await attempt(() => api.leaveRequests(tab.value === 'All' ? undefined : tab.value))) ?? []
  loading.value = false
}

async function decide(r: LeaveRequest, approve: boolean) {
  const ok = await attempt(
    () => (approve ? api.approveLeave(r.id) : api.rejectLeave(r.id)),
    approve ? `Approved ${r.employeeName}'s leave` : `Rejected ${r.employeeName}'s leave`,
  )
  if (ok) await load()
}

async function submit() {
  const ok = await attempt(() => api.requestLeave({ ...form.value, reason: form.value.reason || undefined }), 'Leave requested')
  if (ok) {
    dialog.value = false
    tab.value = 'Pending'
    await load()
  }
}

watch(tab, load)
onMounted(async () => {
  if (isAdmin()) employees.value = (await attempt(() => api.employees())) ?? []
  await load()
})
</script>

<template>
  <div class="d-flex align-center mb-4">
    <v-tabs v-model="tab" color="primary">
      <v-tab value="Pending">Pending</v-tab>
      <v-tab value="Approved">Approved</v-tab>
      <v-tab value="Rejected">Rejected</v-tab>
      <v-tab value="All">All</v-tab>
    </v-tabs>
    <v-spacer />
    <v-btn color="primary" prepend-icon="mdi-plus" @click="dialog = true">Request leave</v-btn>
  </div>

  <v-card border flat>
    <v-progress-linear v-if="loading" indeterminate />
    <v-table>
      <thead>
        <tr>
          <th>Employee</th>
          <th>Type</th>
          <th>Dates</th>
          <th class="text-right">Working days</th>
          <th>Reason</th>
          <th>Status</th>
          <th class="text-right"></th>
        </tr>
      </thead>
      <tbody>
        <tr v-if="!loading && requests.length === 0">
          <td colspan="7" class="text-center text-medium-emphasis py-6">No requests.</td>
        </tr>
        <tr v-for="r in requests" :key="r.id">
          <td>
            <div>{{ r.employeeName }}</div>
            <div class="text-caption text-medium-emphasis">{{ r.employeeCode }}</div>
          </td>
          <td>{{ r.type }}</td>
          <td>{{ formatDate(r.startDate) }} – {{ formatDate(r.endDate) }}</td>
          <td class="text-right">{{ r.days }}</td>
          <td>{{ r.reason ?? '—' }}</td>
          <td>
            <v-chip :color="statusColor[r.status]" size="small" variant="tonal">{{ r.status }}</v-chip>
            <div v-if="r.decidedBy" class="text-caption text-medium-emphasis">by {{ r.decidedBy }}</div>
          </td>
          <td class="text-right">
            <template v-if="r.status === 'Pending' && canDecideLeave(r.employeeId)">
              <v-btn size="small" variant="text" color="error" @click="decide(r, false)">Reject</v-btn>
              <v-btn size="small" variant="flat" color="primary" @click="decide(r, true)">Approve</v-btn>
            </template>
          </td>
        </tr>
      </tbody>
    </v-table>
  </v-card>

  <v-dialog v-model="dialog" max-width="520">
    <v-card title="Request leave">
      <v-card-text class="d-flex flex-column ga-3">
        <v-select v-if="isAdmin()" v-model="form.employeeId" :items="employees" item-value="id" item-title="fullName" label="Employee" />
        <v-select v-model="form.type" :items="['Annual', 'Sick', 'Unpaid']" label="Type" />
        <div class="d-flex ga-3">
          <v-text-field v-model="form.startDate" type="date" label="From" />
          <v-text-field v-model="form.endDate" type="date" label="To" />
        </div>
        <v-text-field v-model="form.reason" label="Reason (optional)" />
        <div class="text-caption text-medium-emphasis">Weekends (per the employee's schedule) and public holidays are not counted.</div>
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn @click="dialog = false">Cancel</v-btn>
        <v-btn color="primary" variant="flat" :disabled="!form.employeeId || !form.startDate || !form.endDate" @click="submit">
          Submit
        </v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>
