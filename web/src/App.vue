<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute } from 'vue-router'
import { useDisplay } from 'vuetify'
import { api } from './api/client'
import { clearSession, isAdmin, session } from './auth'
import ChangePasswordDialog from './components/ChangePasswordDialog.vue'
import { toast } from './notify'
import type { UserRole } from './api/types'

const route = useRoute()
const { mdAndUp, smAndUp } = useDisplay()
const title = computed(() => (route.meta.title as string | undefined) ?? 'TimeLedger')
const drawer = ref<boolean | null>(null)
const passwordDialog = ref(false)
const showShell = computed(() => session.user !== null && !route.meta.public)

const roleLabel: Record<UserRole, string> = { Employee: 'Employee', Supervisor: 'Supervisor', PayrollAdmin: 'Payroll admin' }

const allNav = [
  { to: '/presence', title: 'Presence', icon: 'mdi-account-clock-outline' },
  { to: '/timesheets', title: 'Timesheets', icon: 'mdi-table-clock' },
  { to: '/leave', title: 'Leave', icon: 'mdi-calendar-account-outline' },
  { to: '/integrations', title: 'Integrations', icon: 'mdi-transit-connection-variant', admin: true },
]
const nav = computed(() => allNav.filter((item) => !item.admin || isAdmin()))

async function signOut() {
  try {
    await api.logout()
  } catch {
    // Forget the token here even if the server can't be reached.
  }
  clearSession()
}
</script>

<template>
  <v-app>
    <v-navigation-drawer v-if="showShell" v-model="drawer" :permanent="mdAndUp" :temporary="!mdAndUp" width="232">
      <div class="px-4 py-5">
        <div class="text-h6 font-weight-bold text-primary">TimeLedger</div>
        <div class="text-caption text-medium-emphasis">Time, attendance &amp; payroll sync</div>
      </div>
      <v-divider />
      <v-list nav density="comfortable">
        <v-list-item v-for="item in nav" :key="item.to" :to="item.to" :prepend-icon="item.icon" :title="item.title" />
      </v-list>
    </v-navigation-drawer>

    <v-app-bar v-if="showShell" flat border="b" density="comfortable">
      <v-app-bar-nav-icon v-if="!mdAndUp" aria-label="Open navigation" @click="drawer = !drawer" />
      <v-app-bar-title>{{ title }}</v-app-bar-title>
      <template #append>
        <span v-if="mdAndUp" class="text-caption text-medium-emphasis mr-4">All times WIB (UTC+7)</span>
        <v-menu>
          <template #activator="{ props }">
            <v-btn v-bind="props" variant="text" class="text-none" aria-label="Account menu">
              <v-icon icon="mdi-account-circle-outline" :start="smAndUp" />
              <span v-if="smAndUp">{{ session.user?.fullName ?? session.user?.email }}</span>
            </v-btn>
          </template>
          <v-list min-width="240">
            <v-list-item :title="session.user?.email" :subtitle="session.user ? roleLabel[session.user.role] : ''" />
            <v-divider />
            <v-list-item prepend-icon="mdi-lock-reset" title="Change password" @click="passwordDialog = true" />
            <v-list-item prepend-icon="mdi-logout" title="Sign out" @click="signOut" />
          </v-list>
        </v-menu>
      </template>
    </v-app-bar>

    <v-main class="bg-grey-lighten-5">
      <v-container fluid class="pa-6">
        <router-view v-if="showShell || route.meta.public" />
      </v-container>
    </v-main>

    <ChangePasswordDialog v-if="showShell" v-model="passwordDialog" />
    <v-snackbar v-model="toast.show" :color="toast.color" timeout="4000">{{ toast.text }}</v-snackbar>
  </v-app>
</template>
