import { filterToQuery, filtersEqual, isFilterActive, normalizeFilter } from './ticketFilters'

describe('normalizeFilter', () => {
  it('drops empty members and sorts multi-value members', () => {
    expect(normalizeFilter({ search: '  ', statuses: [], priorities: ['High', 'Critical'], queueId: '' }))
      .toEqual({ priorities: ['Critical', 'High'] })
  })

  it('lets "unassigned" win over an assignee, because the API cannot honour both', () => {
    expect(normalizeFilter({ unassigned: true, assignedTechnicianId: 'tech-1' })).toEqual({ unassigned: true })
    expect(normalizeFilter({ unassigned: false, assignedTechnicianId: 'tech-1' })).toEqual({ assignedTechnicianId: 'tech-1' })
  })
})

describe('filtersEqual', () => {
  it('ignores ordering and blank members', () => {
    expect(filtersEqual({ statuses: ['Pending', 'New'] }, { statuses: ['New', 'Pending'], search: '' })).toBe(true)
    expect(filtersEqual({ statuses: ['New'] }, { statuses: ['Pending'] })).toBe(false)
  })
})

describe('isFilterActive', () => {
  it('is false for an empty filter and true once anything is set', () => {
    expect(isFilterActive({})).toBe(false)
    expect(isFilterActive({ search: '   ' })).toBe(false)
    expect(isFilterActive({ search: 'vpn' })).toBe(true)
  })
})

describe('filterToQuery', () => {
  it('maps members onto the API parameter names, repeating multi-value ones', () => {
    const query = filterToQuery({ search: 'vpn', statuses: ['New', 'Pending'], priorities: ['High'], queueId: 'queue-1', categoryId: 'category-1' })

    expect(query).toBe('page=1&pageSize=200&q=vpn&status=New&status=Pending&priority=High&queueId=queue-1&categoryId=category-1')
  })

  it('maps the 360° page filters onto ciId and requester', () => {
    expect(filterToQuery({ ciId: 'ci-1' })).toBe('page=1&pageSize=200&ciId=ci-1')
    expect(filterToQuery({ requesterId: 'enduser1' })).toBe('page=1&pageSize=200&requester=enduser1')
  })

  it('sends unassigned instead of an assignee and keeps pagination explicit', () => {
    expect(filterToQuery({ unassigned: true, assignedTechnicianId: 'tech-1' }, 2, 25))
      .toBe('page=2&pageSize=25&unassigned=true')
    expect(filterToQuery({})).toBe('page=1&pageSize=200')
  })
})
