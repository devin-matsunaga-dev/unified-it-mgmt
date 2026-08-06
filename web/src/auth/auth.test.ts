import { normalizeRoles } from './auth'

describe('role authorization', () => {
  it('keeps only application roles returned by the API', () => {
    expect(normalizeRoles(['Admin', 'offline_access', 'Technician'])).toEqual(['Admin', 'Technician'])
  })

  it('returns no roles for a malformed API response', () => {
    expect(normalizeRoles('Admin')).toEqual([])
  })
})
