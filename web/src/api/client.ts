import { userManager } from '../auth/auth'

const apiBaseUrl = (import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5000').replace(/\/$/, '')

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    /** Field errors from an RFC 7807 validation problem, so a form can show them beside their input. */
    public readonly errors?: Record<string, string[]>,
  ) {
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
  return read<T>(response)
}

/**
 * Multipart uploads: the browser has to set Content-Type itself so the multipart boundary matches the
 * body, which is why this cannot go through apiRequest's JSON header.
 */
export async function apiUpload<T>(path: string, body: FormData): Promise<T> {
  const user = await userManager.getUser()
  if (!user || user.expired) {
    throw new ApiError(401, 'Your session has expired. Please sign in again.')
  }

  const response = await fetch(`${apiBaseUrl}${path}`, {
    method: 'POST',
    body,
    headers: { Authorization: `Bearer ${user.access_token}` },
  })
  return read<T>(response)
}

async function read<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as
      { detail?: string; title?: string; errors?: Record<string, string[]> } | null
    const firstFieldError = Object.values(problem?.errors ?? {}).flat()[0]
    throw new ApiError(
      response.status,
      problem?.detail ?? firstFieldError ?? problem?.title ?? 'The request could not be completed.',
      problem?.errors,
    )
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}
