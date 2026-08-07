import { QueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'

function showError(error: Error) {
  toast.error('Something went wrong', { description: error.message })
}

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: { staleTime: 30_000, retry: (count, error) => count < 2 && !('status' in error && error.status === 401) },
    mutations: { onError: showError },
  },
})

queryClient.getQueryCache().subscribe((event) => {
  if (event.type === 'updated' && event.action.type === 'error' && !event.query.meta?.suppressErrorToast) {
    showError(event.action.error)
  }
})
