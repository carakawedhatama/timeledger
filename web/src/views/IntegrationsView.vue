<script setup lang="ts">
import { onMounted, onUnmounted, ref, watch } from 'vue'
import { api } from '../api/client'
import type { Delivery, DeliveryStatus, Integration } from '../api/types'
import { formatDateTime } from '../format'
import { attempt } from '../notify'

const integrations = ref<Integration[]>([])
const deliveries = ref<Delivery[]>([])
const filter = ref<DeliveryStatus | null>(null)
let timer: number | undefined

const statusColor: Record<DeliveryStatus, string> = {
  Pending: 'warning',
  Succeeded: 'success',
  Skipped: 'grey',
  DeadLettered: 'error',
}

const providerLabel = { GenericWebhook: 'Signed webhook', GustoStyle: 'Payroll (Gusto-style)' } as const

async function loadDeliveries() {
  const result = await attempt(() => api.deliveries(filter.value ?? undefined))
  if (result) deliveries.value = result
}

async function retry(d: Delivery) {
  const ok = await attempt(() => api.retryDelivery(d.id).then(() => true), 'Requeued')
  if (ok) await loadDeliveries()
}

watch(filter, loadDeliveries)
onMounted(async () => {
  integrations.value = (await attempt(() => api.integrations())) ?? []
  await loadDeliveries()
  timer = window.setInterval(loadDeliveries, 5_000)
})
onUnmounted(() => window.clearInterval(timer))
</script>

<template>
  <v-row class="mb-2">
    <v-col v-for="i in integrations" :key="i.id" cols="12" md="6">
      <v-card border flat class="pa-4">
        <div class="d-flex align-center">
          <div>
            <div class="text-subtitle-1 font-weight-medium">{{ i.name }}</div>
            <div class="text-caption text-medium-emphasis">{{ providerLabel[i.provider] }} · secret {{ i.secretHint }}</div>
          </div>
          <v-spacer />
          <v-chip :color="i.isEnabled ? 'success' : undefined" size="small" variant="tonal">
            {{ i.isEnabled ? 'Enabled' : 'Paused' }}
          </v-chip>
        </div>
        <div class="text-caption mt-3 text-truncate">{{ i.endpointUrl }}</div>
        <div class="mt-2 d-flex ga-1 flex-wrap">
          <v-chip v-for="t in i.eventTypes" :key="t" size="x-small" label>{{ t }}</v-chip>
        </div>
      </v-card>
    </v-col>
  </v-row>

  <v-card border flat>
    <v-card-title class="d-flex align-center">
      Delivery log
      <v-spacer />
      <v-chip-group v-model="filter" selected-class="text-primary">
        <v-chip value="Pending" size="small" filter>Pending</v-chip>
        <v-chip value="Succeeded" size="small" filter>Succeeded</v-chip>
        <v-chip value="DeadLettered" size="small" filter>Dead-lettered</v-chip>
      </v-chip-group>
    </v-card-title>
    <v-card-subtitle class="pb-2">
      Outbox → delivery rows → signed POST with exponential backoff. Refreshes every 5 s.
    </v-card-subtitle>
    <v-table density="comfortable">
      <thead>
        <tr>
          <th>Created</th>
          <th>Connection</th>
          <th>Event</th>
          <th>Status</th>
          <th class="text-right">Attempts</th>
          <th>Last result</th>
          <th>Next attempt</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        <tr v-if="deliveries.length === 0">
          <td colspan="8" class="text-center text-medium-emphasis py-6">
            No deliveries yet. Close a pay period or approve leave to publish an event.
          </td>
        </tr>
        <tr v-for="d in deliveries" :key="d.id">
          <td class="text-no-wrap">{{ formatDateTime(d.createdAt) }}</td>
          <td>{{ d.connectionName }}</td>
          <td><code>{{ d.eventType }}</code></td>
          <td><v-chip :color="statusColor[d.status]" size="small" variant="tonal">{{ d.status }}</v-chip></td>
          <td class="text-right">{{ d.attempts }}</td>
          <td class="text-caption" style="max-width: 280px">
            <span v-if="d.lastStatusCode" class="font-weight-medium">{{ d.lastStatusCode }}</span>
            {{ d.lastError ?? '' }}
          </td>
          <td class="text-no-wrap">{{ d.status === 'Pending' ? formatDateTime(d.nextAttemptAt) : '—' }}</td>
          <td class="text-right">
            <v-btn v-if="d.status === 'DeadLettered'" size="small" variant="outlined" @click="retry(d)">Retry</v-btn>
          </td>
        </tr>
      </tbody>
    </v-table>
  </v-card>
</template>
