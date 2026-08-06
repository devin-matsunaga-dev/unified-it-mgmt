import { vi } from 'vitest'

const getUser = vi.fn()
vi.mock('../auth/auth', () => ({ userManager: { getUser } }))

describe('apiRequest', () => {
  it('fails without making a request when the session is expired', async () => {
    getUser.mockResolvedValue({ expired: true })
    const fetchSpy = vi.spyOn(globalThis, 'fetch')
    const { apiRequest } = await import('./client')
    await expect(apiRequest('/api/me')).rejects.toMatchObject({ status: 401 })
    expect(fetchSpy).not.toHaveBeenCalled()
  })
})
