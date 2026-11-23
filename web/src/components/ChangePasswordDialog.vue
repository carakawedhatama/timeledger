<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '../api/client'
import { clearSession } from '../auth'
import { attempt } from '../notify'

const open = defineModel<boolean>({ required: true })
const current = ref('')
const next = ref('')
const repeat = ref('')
const busy = ref(false)
const ready = computed(() => current.value !== '' && next.value.length >= 12 && next.value === repeat.value)

watch(open, () => {
  current.value = next.value = repeat.value = ''
})

async function submit() {
  if (!ready.value) return
  busy.value = true
  const ok = await attempt(() => api.changePassword(current.value, next.value).then(() => true), 'Password changed. Sign in again.')
  busy.value = false
  if (ok) {
    open.value = false
    clearSession()
  }
}
</script>

<template>
  <v-dialog v-model="open" max-width="440">
    <v-card title="Change password">
      <v-form @submit.prevent="submit">
        <v-card-text class="d-flex flex-column ga-2">
          <v-text-field v-model="current" label="Current password" type="password" autocomplete="current-password" />
          <v-text-field v-model="next" label="New password" type="password" autocomplete="new-password" hint="At least 12 characters" persistent-hint />
          <v-text-field
            v-model="repeat"
            label="Repeat new password"
            type="password"
            autocomplete="new-password"
            :error-messages="repeat && repeat !== next ? 'Passwords do not match' : ''"
          />
          <div class="text-caption text-medium-emphasis">This signs you out on every device.</div>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="open = false">Cancel</v-btn>
          <v-btn type="submit" color="primary" variant="flat" :loading="busy" :disabled="!ready">Change password</v-btn>
        </v-card-actions>
      </v-form>
    </v-card>
  </v-dialog>
</template>
