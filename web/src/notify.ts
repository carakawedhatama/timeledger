import { reactive } from 'vue'

export const toast = reactive({ show: false, text: '', color: 'success' as 'success' | 'error' })

export function notify(text: string, color: 'success' | 'error' = 'success') {
  toast.text = text
  toast.color = color
  toast.show = true
}

/** Runs an action and reports failure in the toast instead of throwing into the template. */
export async function attempt<T>(action: () => Promise<T>, success?: string): Promise<T | undefined> {
  try {
    const result = await action()
    if (success) notify(success)
    return result
  } catch (error) {
    notify(error instanceof Error ? error.message : String(error), 'error')
    return undefined
  }
}
