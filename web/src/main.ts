import { createApp, watch } from 'vue'
import { createRouter, createWebHistory } from 'vue-router'
import { createVuetify } from 'vuetify'
import '@mdi/font/css/materialdesignicons.css'
import 'vuetify/styles'
import App from './App.vue'
import { api } from './api/client'
import { clearSession, isAdmin, session } from './auth'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', component: () => import('./views/LoginView.vue'), meta: { title: 'Sign in', public: true } },
    { path: '/', redirect: '/presence' },
    { path: '/presence', component: () => import('./views/PresenceView.vue'), meta: { title: 'Presence' } },
    { path: '/timesheets', component: () => import('./views/TimesheetsView.vue'), meta: { title: 'Timesheets' } },
    { path: '/leave', component: () => import('./views/LeaveView.vue'), meta: { title: 'Leave' } },
    { path: '/integrations', component: () => import('./views/IntegrationsView.vue'), meta: { title: 'Integrations', admin: true } },
    { path: '/:pathMatch(.*)*', redirect: '/presence' },
  ],
})

router.beforeEach((to) => {
  if (to.meta.public) return session.user ? '/presence' : true
  if (!session.user) return { path: '/login', query: { next: to.fullPath } }
  return to.meta.admin && !isAdmin() ? '/presence' : true
})

// Sign-out, expiry and revocation all end here. No "next": the next person to sign in may be someone else.
watch(
  () => session.user,
  (user) => {
    if (!user) void router.replace('/login')
  },
)

const vuetify = createVuetify({
  theme: {
    defaultTheme: 'light',
    themes: {
      light: { colors: { primary: '#0F766E', secondary: '#334155' } },
    },
  },
  defaults: {
    VCard: { rounded: 'lg' },
    VBtn: { rounded: 'lg' },
  },
})

async function start() {
  if (session.token) {
    try {
      session.user = await api.me()
    } catch {
      clearSession()
    }
  }
  createApp(App).use(router).use(vuetify).mount('#app')
}

void start()
