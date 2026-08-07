import type { CiAttributeDefinition, CiCustomField } from '../../api/assets'
import { ciValuePayload, validateAttributes, validateCiCustomFields } from './ciFields'

const serverAttributes: CiAttributeDefinition[] = [
  { key: 'hostname', label: 'Hostname', kind: 'Text', isRequired: true },
  { key: 'cpuCores', label: 'CPU cores', kind: 'Integer', isRequired: true },
]

const managementIp: CiAttributeDefinition[] = [
  { key: 'managementIp', label: 'Management IP', kind: 'IpAddress', isRequired: true },
]

function customField(overrides: Partial<CiCustomField> = {}): CiCustomField {
  return { id: 'field-1', ciType: 'Server', key: 'rack_unit', label: 'Rack unit', type: 'Text', isRequired: false, options: [], sortOrder: 0, ...overrides }
}

describe('validateAttributes', () => {
  it('reports a missing required attribute', () => {
    expect(validateAttributes(serverAttributes, { cpuCores: '8' })).toEqual({ hostname: 'Hostname is required.' })
  })

  it('treats whitespace as missing', () => {
    expect(validateAttributes(serverAttributes, { hostname: '  ', cpuCores: '8' })).toEqual({ hostname: 'Hostname is required.' })
  })

  it('rejects a non-integer where the schema wants a whole number', () => {
    expect(validateAttributes(serverAttributes, { hostname: 'app-01', cpuCores: '8.5' }))
      .toEqual({ cpuCores: 'CPU cores must be a whole number of zero or more.' })
  })

  it('rejects an out-of-range IPv4 octet the server would also reject', () => {
    expect(validateAttributes(managementIp, { managementIp: '10.0.0.999' }))
      .toEqual({ managementIp: 'Management IP must be a valid IPv4 or IPv6 address.' })
  })

  it('accepts a valid IPv4 and IPv6 address', () => {
    expect(validateAttributes(managementIp, { managementIp: '10.20.0.1' })).toEqual({})
    expect(validateAttributes(managementIp, { managementIp: '2001:db8::1' })).toEqual({})
  })

  it('passes a complete attribute set', () => {
    expect(validateAttributes(serverAttributes, { hostname: 'app-01', cpuCores: '8' })).toEqual({})
  })
})

describe('validateCiCustomFields', () => {
  it('reports a missing required field', () => {
    expect(validateCiCustomFields([customField({ isRequired: true })], {}))
      .toEqual({ rack_unit: 'Rack unit is required.' })
  })

  it('rejects a select value outside the declared options', () => {
    const field = customField({ type: 'Select', options: ['Primary', 'Secondary'] })
    expect(validateCiCustomFields([field], { rack_unit: 'Tertiary' }))
      .toEqual({ rack_unit: 'Rack unit must be one of: Primary, Secondary.' })
  })

  it('rejects a date that is not yyyy-MM-dd', () => {
    expect(validateCiCustomFields([customField({ type: 'Date' })], { rack_unit: '07/08/2026' }))
      .toEqual({ rack_unit: 'Rack unit must be a date in yyyy-MM-dd format.' })
  })
})

describe('ciValuePayload', () => {
  it('drops blanks and keys left behind by a previously selected type', () => {
    expect(ciValuePayload(['hostname', 'cpuCores'], { hostname: ' app-01 ', cpuCores: '', portCount: '48' }))
      .toEqual({ hostname: 'app-01' })
  })
})
