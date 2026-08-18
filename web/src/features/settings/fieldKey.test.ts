import { describe, expect, it } from 'vitest'
import { fieldKeyMaxLength, toFieldKey } from './fieldKey'

/** The rule the server enforces; every generated key has to satisfy it or the form posts a 400. */
const serverRule = /^[a-zA-Z][a-zA-Z0-9_]*$/

describe('toFieldKey', () => {
  it('lower-cases and joins words with underscores', () => {
    expect(toFieldKey('Hardware type')).toBe('hardware_type')
    expect(toFieldKey('Purchase Order')).toBe('purchase_order')
  })

  it('drops punctuation rather than escaping it', () => {
    expect(toFieldKey('CPU cores (max)')).toBe('cpu_cores_max')
    expect(toFieldKey('Owner / custodian')).toBe('owner_custodian')
  })

  it('collapses runs of separators and never ends in an underscore', () => {
    expect(toFieldKey('Warranty   —   expiry!!!')).toBe('warranty_expiry')
    expect(toFieldKey('Trailing spaces   ')).toBe('trailing_spaces')
  })

  /** Folded, not dropped: losing the whole character would turn "Café" into "caf". */
  it('folds accents to their base letter', () => {
    expect(toFieldKey('Café type')).toBe('cafe_type')
    expect(toFieldKey('Año de compra')).toBe('ano_de_compra')
  })

  /** The server requires a letter first, so digits before one cannot be kept. */
  it('removes anything before the first letter', () => {
    expect(toFieldKey('3D printer')).toBe('d_printer')
    expect(toFieldKey('_leading underscore')).toBe('leading_underscore')
  })

  /**
   * A label that yields nothing usable returns empty rather than an invented key. The key is
   * permanent, so a person choosing it beats this function guessing.
   */
  it('returns empty when nothing usable survives', () => {
    expect(toFieldKey('###')).toBe('')
    expect(toFieldKey('123')).toBe('')
    expect(toFieldKey('')).toBe('')
  })

  it('truncates to the length the server accepts, without a trailing underscore', () => {
    const key = toFieldKey('a'.repeat(80))
    expect(key).toHaveLength(fieldKeyMaxLength)

    // A truncation landing on a separator would otherwise leave the key ending in one.
    const onBoundary = toFieldKey(`${'a'.repeat(fieldKeyMaxLength - 1)} tail`)
    expect(onBoundary.endsWith('_')).toBe(false)
    expect(onBoundary.length).toBeLessThanOrEqual(fieldKeyMaxLength)
  })

  it('always produces something the server would accept', () => {
    const labels = [
      'Hardware type', 'CPU cores (max)', 'Café type', '3D printer', 'Owner / custodian',
      'Warranty   —   expiry!!!', 'a'.repeat(80), 'Año de compra',
    ]

    for (const label of labels) {
      const key = toFieldKey(label)
      expect(key === '' || serverRule.test(key)).toBe(true)
      expect(key).toBe(key.toLowerCase())
    }
  })
})
