import { BarcodeFormat, MultiFormatWriter } from '@zxing/library'
import { createReader, decode } from './useQrCamera'

/** Turns a column pattern (true = dark) into the RGBA frame shape a canvas hands the decoder. */
function frameFromColumns(columns: boolean[], height: number, quiet: number): ImageData {
  const width = columns.length + quiet * 2
  const data = new Uint8ClampedArray(width * height * 4).fill(255)
  for (let y = 0; y < height; y += 1) {
    for (let x = 0; x < columns.length; x += 1) {
      if (!columns[x]) continue
      const at = (y * width + (x + quiet)) * 4
      data[at] = 0
      data[at + 1] = 0
      data[at + 2] = 0
    }
  }
  return { data, width, height, colorSpace: 'srgb' } as ImageData
}

/**
 * A minimal Code 39 encoder, written here because @zxing/library ships decoders for the 1D families
 * but writers only for QR, Data Matrix and Aztec — so there is no way to generate the fixture from
 * the library under test. Nine elements per character, alternating bar and space, each narrow or
 * wide, one narrow space between characters, `*` as the start and stop symbol.
 *
 * A mistake in this table cannot produce a false pass: a wrong pattern decodes to nothing or to the
 * wrong text, and the assertion fails either way.
 */
const code39: Record<string, string> = {
  '0': 'nnnwwnwnn', '1': 'wnnwnnnnw', '2': 'nnwwnnnnw', '3': 'wnwwnnnnn', '4': 'nnnwwnnnw',
  '5': 'wnnwwnnnn', '6': 'nnwwwnnnn', '7': 'nnnwnnwnw', '8': 'wnnwnnwnn', '9': 'nnwwnnwnn',
  A: 'wnnnnwnnw', B: 'nnwnnwnnw', C: 'wnwnnwnnn', D: 'nnnnwwnnw', E: 'wnnnwwnnn',
  F: 'nnwnwwnnn', G: 'nnnnnwwnw', H: 'wnnnnwwnn', I: 'nnwnnwwnn', J: 'nnnnwwwnn',
  K: 'wnnnnnnww', L: 'nnwnnnnww', M: 'wnwnnnnwn', N: 'nnnnwnnww', O: 'wnnnwnnwn',
  P: 'nnwnwnnwn', Q: 'nnnnnnwww', R: 'wnnnnnwwn', S: 'nnwnnnwwn', T: 'nnnnwnwwn',
  U: 'wwnnnnnnw', V: 'nwwnnnnnw', W: 'wwwnnnnnn', X: 'nwnnwnnnw', Y: 'wwnnwnnnn',
  Z: 'nwwnwnnnn', '-': 'nwnnnnwnw', '.': 'wwnnnnwnn', ' ': 'nwwnnnwnn', '*': 'nwnnwnwnn',
}

function encodeCode39(text: string, narrow = 3, wide = 9): boolean[] {
  const columns: boolean[] = []
  const push = (dark: boolean, width: number) => {
    for (let index = 0; index < width; index += 1) columns.push(dark)
  }
  for (const character of `*${text}*`) {
    const pattern = code39[character]
    if (!pattern) throw new Error(`Code 39 carries no pattern for '${character}'`)
    // Elements alternate bar, space, bar … starting on a bar.
    pattern.split('').forEach((size, index) => push(index % 2 === 0, size === 'w' ? wide : narrow))
    push(false, narrow) // the inter-character gap
  }
  return columns
}

describe('barcode decoding', () => {
  /**
   * The reason ZXing replaced jsQR. A manufacturer's label on a new device is 1D — Code 39 is what a
   * Dell service tag wears — and jsQR reads QR only, so the old scanner could not see one at all.
   */
  it('reads a Code 39 service tag, which jsQR could never do', () => {
    const frame = frameFromColumns(encodeCode39('7XKLM92'), 120, 30)
    expect(decode(createReader(), frame)).toBe('7XKLM92')
  })

  it('reads a longer alphanumeric serial off the same symbology', () => {
    const frame = frameFromColumns(encodeCode39('5CD1234ABC'), 120, 30)
    expect(decode(createReader(), frame)).toBe('5CD1234ABC')
  })

  /** Our own printed labels are QR, and swapping the decoder must not cost us those. */
  it('still reads the QR our own labels carry', () => {
    const target = 'https://192.168.128.199:5173/assets/018f2c7a-0000-7000-8000-000000000001'
    const matrix = new MultiFormatWriter().encode(target, BarcodeFormat.QR_CODE, 300, 300, new Map())
    const data = new Uint8ClampedArray(300 * 300 * 4).fill(255)
    for (let y = 0; y < 300; y += 1) {
      for (let x = 0; x < 300; x += 1) {
        if (!matrix.get(x, y)) continue
        const at = (y * 300 + x) * 4
        data[at] = 0
        data[at + 1] = 0
        data[at + 2] = 0
      }
    }
    const frame = { data, width: 300, height: 300, colorSpace: 'srgb' } as ImageData

    expect(decode(createReader(), frame)).toBe(target)
  })

  it('returns null for a frame carrying nothing, rather than throwing', () => {
    const blank = new Uint8ClampedArray(100 * 100 * 4).fill(255)
    expect(decode(createReader(), { data: blank, width: 100, height: 100, colorSpace: 'srgb' } as ImageData)).toBeNull()
  })
})
