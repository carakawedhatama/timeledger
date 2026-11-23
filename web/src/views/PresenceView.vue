<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { api } from '../api/client'
import type { ClockEventType, PresenceEntry } from '../api/types'
import { canActFor, isAdmin } from '../auth'
import { formatTime } from '../format'
import { attempt } from '../notify'

const entries = ref<PresenceEntry[]>([])
const loading = ref(true)
const busy = ref<string | null>(null)
let timer: number | undefined

const inCount = computed(() => entries.value.filter((e) => e.status === 'in').length)

async function load() {
  const result = await attempt(() => api.presence())
  if (result) entries.value = result
  loading.value = false
}

async function punch(entry: PresenceEntry) {
  const type: ClockEventType = entry.status === 'in' ? 'ClockOut' : 'ClockIn'
  busy.value = entry.employeeId
  const verb = type === 'ClockIn' ? 'clocked in' : 'clocked out'
  await attempt(() => api.punch(entry.employeeId, type), `${entry.fullName} ${verb}`)
  busy.value = null
  await load()
}

onMounted(() => {
  load()
  timer = window.setInterval(load, 15_000)
})
onUnmounted(() => window.clearInterval(timer))
</script>

<template>
  <v-row class="mb-2">
    <v-col cols="12" sm="4">
      <v-card border flat class="pa-4">
        <div class="text-overline">Clocked in</div>
        <div class="text-h4 font-weight-bold text-primary">{{ inCount }}</div>
      </v-card>
    </v-col>
    <v-col cols="12" sm="4">
      <v-card border flat class="pa-4">
        <div class="text-overline">Not in</div>
        <div class="text-h4 font-weight-bold">{{ entries.length - inCount }}</div>
      </v-card>
    </v-col>
    <v-col cols="12" sm="4">
      <v-card border flat class="pa-4">
        <div class="text-overline">{{ isAdmin() ? 'Active employees' : 'Visible to you' }}</div>
        <div class="text-h4 font-weight-bold">{{ entries.length }}</div>
      </v-card>
    </v-col>
  </v-row>

  <v-card border flat>
    <v-card-title class="d-flex align-center flex-wrap">
      Who's in now
      <v-spacer />
      <span class="text-caption text-medium-emphasis">Served from Redis · refreshes every 15 s</span>
    </v-card-title>
    <v-table>
      <thead>
        <tr>
          <th class="d-none d-sm-table-cell">Code</th>
          <th>Name</th>
          <th>Status</th>
          <th class="d-none d-sm-table-cell">Since</th>
          <th class="text-right">Action</th>
        </tr>
      </thead>
      <tbody>
        <tr v-if="loading"><td colspan="5"><v-progress-linear indeterminate /></td></tr>
        <tr v-for="e in entries" :key="e.employeeId">
          <td class="text-medium-emphasis d-none d-sm-table-cell">{{ e.employeeCode }}</td>
          <td>{{ e.fullName }}</td>
          <td>
            <v-chip :color="e.status === 'in' ? 'success' : undefined" size="small" variant="tonal">
              {{ e.status === 'in' ? 'In' : 'Out' }}
            </v-chip>
          </td>
          <td class="d-none d-sm-table-cell">{{ e.since ? formatTime(e.since) : '—' }}</td>
          <td class="text-right">
            <v-btn
              v-if="canActFor(e.employeeId)"
              size="small"
              :variant="e.status === 'in' ? 'outlined' : 'flat'"
              color="primary"
              :loading="busy === e.employeeId"
              @click="punch(e)"
            >
              {{ e.status === 'in' ? 'Clock out' : 'Clock in' }}
            </v-btn>
          </td>
        </tr>
      </tbody>
    </v-table>
  </v-card>
</template>
