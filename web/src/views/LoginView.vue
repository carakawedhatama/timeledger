<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api } from '../api/client'
import { startSession } from '../auth'

const router = useRouter()
const route = useRoute()
const email = ref('')
const password = ref('')
const error = ref('')
const busy = ref(false)

function destination() {
  const next = route.query.next
  // Only paths within this app: "//host" would be another site.
  return typeof next === 'string' && next.startsWith('/') && !next.startsWith('//') ? next : '/presence'
}

async function submit() {
  busy.value = true
  error.value = ''
  try {
    const result = await api.login(email.value, password.value)
    startSession(result.accessToken, result.user)
    await router.replace(destination())
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="d-flex justify-center align-center" style="min-height: 80vh">
    <v-card border flat width="400" max-width="100%" class="pa-6">
      <h1 class="text-h5 font-weight-bold text-primary">TimeLedger</h1>
      <p class="text-body-2 text-medium-emphasis mb-6">Sign in to continue</p>
      <v-form @submit.prevent="submit">
        <v-alert v-if="error" type="error" variant="tonal" density="compact" class="mb-4" role="alert">{{ error }}</v-alert>
        <v-text-field v-model="email" label="Email" type="email" autocomplete="username" autofocus />
        <v-text-field v-model="password" label="Password" type="password" autocomplete="current-password" />
        <v-btn type="submit" color="primary" block size="large" :loading="busy" :disabled="!email || !password">Sign in</v-btn>
      </v-form>
    </v-card>
  </div>
</template>
