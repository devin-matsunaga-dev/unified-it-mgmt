import { userManager } from '../auth/auth'

const apiBaseUrl = (import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5000').replace(/\/$/, '')

export class ApiError extends Error {
  constructor(public readonly status: number, message: string) {
    super(message)
  }
}

export async function apiRequest<T>(path: string, init?: RequestInit): Promise<T> {
  const user = await userManager.getUser()
  if (!user || user.expired) {
    throw new ApiError(401, 'Your session has expired. Please sign in again.')
  }

  const response = await fetch(`${apiBaseUrl}${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${user.access_token}`, ...init?.headers },
  })
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as { detail?: string; title?: string } | null
    throw new ApiError(response.status, problem?.detail ?? problem?.title ?? 'The request could not be completed.')
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}
