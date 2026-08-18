import { describe, expect, it } from 'vitest'
import { Pin } from 'lucide-react'
import { defaultTileIcon, tileIcon, tileIcons } from './ciTileIcons'

describe('tileIcons', () => {
  it('offers a curated set with unique keys and readable labels', () => {
    const keys = tileIcons.map((option) => option.key)
    expect(new Set(keys).size).toBe(keys.length)
    for (const option of tileIcons) expect(option.label.length).toBeGreaterThan(0)
  })

  it('names a default that is one of the options', () => {
    expect(tileIcons.some((option) => option.key === defaultTileIcon)).toBe(true)
  })
})

describe('tileIcon', () => {
  it('resolves a known key', () => {
    expect(tileIcon('laptop')).toBe(tileIcons.find((option) => option.key === 'laptop')!.icon)
  })

  /**
   * Tiles live in the reader's own browser and outlive any one release, so a key naming an icon this
   * version no longer offers has to draw as something rather than fail.
   */
  it('falls back to a pin for an unknown or missing key', () => {
    expect(tileIcon('an-icon-that-was-retired')).toBe(Pin)
    expect(tileIcon(undefined)).toBe(Pin)
  })
})
