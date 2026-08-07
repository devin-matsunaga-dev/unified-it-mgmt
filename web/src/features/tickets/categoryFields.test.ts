import type { TicketCategory, TicketCustomField } from '../../api/helpdesk'
import { customFieldPayload, findCategory, flattenCategories, validateCustomFields } from './categoryFields'

const field = (overrides: Partial<TicketCustomField>): TicketCustomField => ({
  id: 'field-1', categoryId: 'category-1', key: 'asset_tag', label: 'Asset tag', type: 'Text', isRequired: false, options: [], sortOrder: 1, ...overrides,
})
const tree: TicketCategory[] = [
  { id: 'root', name: 'Hardware', parentId: null, isActive: true, sortOrder: 1, fields: [], children: [
    { id: 'child', name: 'Laptop issue', parentId: 'root', isActive: true, sortOrder: 1, fields: [field({})], children: [] },
  ] },
]

describe('flattenCategories', () => {
  it('returns every node depth-first with its nesting level', () => {
    expect(flattenCategories(tree)).toEqual([
      { id: 'root', name: 'Hardware', depth: 0 },
      { id: 'child', name: 'Laptop issue', depth: 1 },
    ])
  })
})

describe('findCategory', () => {
  it('finds a nested category and returns null for an unknown id', () => {
    expect(findCategory(tree, 'child')?.name).toBe('Laptop issue')
    expect(findCategory(tree, 'missing')).toBeNull()
    expect(findCategory(tree, null)).toBeNull()
  })
})

describe('validateCustomFields', () => {
  it('reports a missing required value', () => {
    expect(validateCustomFields([field({ isRequired: true })], {})).toEqual({ asset_tag: 'Asset tag is required.' })
  })

  it('accepts a blank optional value', () => {
    expect(validateCustomFields([field({})], { asset_tag: '  ' })).toEqual({})
  })

  it('rejects values that do not match the field type', () => {
    const fields = [
      field({ id: 'f1', key: 'count', label: 'Count', type: 'Number' }),
      field({ id: 'f2', key: 'seen_on', label: 'Seen on', type: 'Date' }),
      field({ id: 'f3', key: 'floor', label: 'Floor', type: 'Select', options: ['First', 'Second'] }),
    ]
    expect(validateCustomFields(fields, { count: 'twelve', seen_on: '07/08/2026', floor: 'Third' })).toEqual({
      count: 'Count must be a number.',
      seen_on: 'Seen on must be a date in yyyy-MM-dd format.',
      floor: 'Floor must be one of: First, Second.',
    })
  })

  it('accepts well-formed values of every type', () => {
    const fields = [
      field({ id: 'f1', key: 'count', label: 'Count', type: 'Number' }),
      field({ id: 'f2', key: 'seen_on', label: 'Seen on', type: 'Date' }),
      field({ id: 'f3', key: 'floor', label: 'Floor', type: 'Select', options: ['First'] }),
    ]
    expect(validateCustomFields(fields, { count: '-12.5', seen_on: '2026-08-07', floor: 'First' })).toEqual({})
  })
})

describe('customFieldPayload', () => {
  it('trims values and drops blanks and values from other categories', () => {
    expect(customFieldPayload([field({})], { asset_tag: ' LT-4417 ', stale_key: 'x' })).toEqual({ asset_tag: 'LT-4417' })
    expect(customFieldPayload([field({})], { asset_tag: '' })).toEqual({})
  })
})
