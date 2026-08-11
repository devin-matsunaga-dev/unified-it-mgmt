import { userManager } from '../auth/auth'

/** Exported so the SignalR hub connects to the same host these calls do, from one definition. */
export const apiBaseUrl = (import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5000').replace(/\/$/, '')

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

/** A generated file plus the name the server gave it. */
export type DownloadedFile = { blob: Blob; fileName: string }

/**
 * Binary responses: a PDF cannot go through apiRequest, which parses every body as JSON, but a failed
 * download still answers with an RFC 7807 problem that has to surface as an ApiError like any other.
 */
export async function apiDownload(path: string, init?: RequestInit): Promise<DownloadedFile> {
  const user = await userManager.getUser()
  if (!user || user.expired) {
    throw new ApiError(401, 'Your session has expired. Please sign in again.')
  }

  const response = await fetch(`${apiBaseUrl}${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${user.access_token}`, ...init?.headers },
  })
  if (!response.ok) {
    await read<unknown>(response)
  }
  return { blob: await response.blob(), fileName: fileNameFrom(response.headers.get('Content-Disposition')) }
}

function fileNameFrom(disposition: string | null) {
  return /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition ?? '')?.[1] ?? 'download.pdf'
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
