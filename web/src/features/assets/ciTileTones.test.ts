import { describe, expect, it } from 'vitest'
import { defaultTileTone, tileToneClasses, tileTones } from './ciTileTones'

describe('tileTones', () => {
  it('offers a curated palette with unique keys and readable labels', () => {
    const keys = tileTones.map((tone) => tone.key)
    expect(new Set(keys).size).toBe(keys.length)
    for (const tone of tileTones) expect(tone.label.length).toBeGreaterThan(0)
  })

  it('defaults to neutral, because a count is not a status', () => {
    expect(defaultTileTone).toBe('slate')
    expect(tileTones.some((tone) => tone.key === defaultTileTone)).toBe(true)
  })

  /**
   * Tailwind scans source text for class names, so a class built from a key at runtime never reaches
   * the stylesheet. Every one has to be written out, and both themes have to be covered.
   */
  it('writes every class out in full, for light and dark', () => {
    for (const tone of tileTones) {
      expect(tone.circle).toMatch(/^bg-[a-z]+-\d+ text-[a-z]+-\d+ dark:bg-\S+ dark:text-\S+$/)
      expect(tone.swatch).toMatch(/^bg-[a-z]+-\d+$/)
    }
  })
})

describe('tileToneClasses', () => {
  it('resolves a known tone', () => {
    expect(tileToneClasses('red')).toBe(tileTones.find((tone) => tone.key === 'red')!.circle)
  })

  /** Tiles outlive any one release, so a retired colour must still draw. */
  it('falls back to neutral for an unknown or missing key', () => {
    const neutral = tileTones.find((tone) => tone.key === defaultTileTone)!.circle
    expect(tileToneClasses('chartreuse')).toBe(neutral)
    expect(tileToneClasses(undefined)).toBe(neutral)
  })
})
