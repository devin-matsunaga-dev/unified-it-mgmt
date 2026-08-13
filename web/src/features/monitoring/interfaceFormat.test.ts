import { describe, expect, it } from 'vitest'
import type { DeviceInterface } from '../../api/monitoring'
import {
  NOT_MEASURED,
  formatBitsPerSecond,
  formatEventRate,
  formatUtilisation,
  interfaceStatusLabel,
  interfaceTone,
  isShut,
  utilisationBarWidth,
} from './interfaceFormat'

function link(overrides: Partial<DeviceInterface> = {}): DeviceInterface {
  return {
    ifIndex: 1, name: 'Gi0/1', alias: null, macAddress: null, interfaceType: 6,
    adminStatus: 'Up', operStatus: 'Up', speedBitsPerSecond: 1_000_000_000,
    bitsInPerSecond: null, bitsOutPerSecond: null, utilisationPercent: null,
    errorsInPerSecond: null, errorsOutPerSecond: null,
    discardsInPerSecond: null, discardsOutPerSecond: null,
    checkId: 'check-1', metricPrefix: 'interface.1.', observedAt: '2026-08-14T09:00:00Z',
    ...overrides,
  }
}

describe('interface formatting', () => {
  it('scales a rate to the unit an operator would say out loud', () => {
    expect(formatBitsPerSecond(0)).toBe('0 bit/s')
    expect(formatBitsPerSecond(800)).toBe('800 bit/s')
    expect(formatBitsPerSecond(12_500_000)).toBe('12.5 Mbit/s')
    expect(formatBitsPerSecond(2_400_000_000)).toBe('2.4 Gbit/s')
  })

  /**
   * Decimal steps, not binary. A gigabit port is 1,000,000,000 bit/s and its utilisation is computed
   * against that — scaling by 1024 here would make a saturated port read as 0.93 Gbit/s of 1 Gbit/s
   * beside a utilisation of 100%.
   */
  it('scales by 1000 rather than 1024', () => {
    expect(formatBitsPerSecond(1_000_000_000)).toBe('1 Gbit/s')
  })

  /**
   * The rule the whole table rests on: a number nobody has measured is not a zero. A rate needs two
   * polls, so a port seen once has a status and no traffic — which is not the same as a quiet port.
   */
  it('renders an unmeasured value as a dash and never as zero', () => {
    expect(formatBitsPerSecond(null)).toBe(NOT_MEASURED)
    expect(formatUtilisation(null)).toBe(NOT_MEASURED)
    expect(formatEventRate(null)).toBe(NOT_MEASURED)
    expect(formatBitsPerSecond(Number.NaN)).toBe(NOT_MEASURED)
  })

  /** One CRC error a minute is 0.02/s, and a column that rounded it away would say the cable is fine. */
  it('keeps a small error rate visible', () => {
    expect(formatEventRate(0)).toBe('0')
    expect(formatEventRate(0.0166)).toBe('0.02/s')
    expect(formatEventRate(0.0001)).toBe('<0.01/s')
  })

  it('states a utilisation to one decimal place', () => {
    expect(formatUtilisation(1.25)).toBe('1.3%')
    expect(formatUtilisation(100)).toBe('100.0%')
  })

  /**
   * The number is deliberately not clamped — above 100% means the speed the agent reports is wrong —
   * but the bar is, because a bar wider than its track is a layout bug rather than a finding.
   */
  it('clamps the bar without clamping the number', () => {
    expect(formatUtilisation(940)).toBe('940.0%')
    expect(utilisationBarWidth(940)).toBe('100%')
    expect(utilisationBarWidth(null)).toBe('0%')
  })
})

describe('interface status', () => {
  it('is green when up and red when it is meant to be up and is not', () => {
    expect(interfaceTone(link({ operStatus: 'Up' }))).toContain('green')
    expect(interfaceTone(link({ operStatus: 'Down' }))).toContain('red')
    expect(interfaceTone(link({ operStatus: 'LowerLayerDown' }))).toContain('red')
  })

  /**
   * A switch ships with every unused port down. Colouring those red — when the alert rule
   * deliberately says nothing about them — teaches an operator to ignore red.
   */
  it('is neutral for a port somebody shut, matching what the alert rule does', () => {
    const shut = link({ operStatus: 'Down', adminStatus: 'Down' })

    expect(isShut(shut)).toBe(true)
    expect(interfaceTone(shut)).toContain('slate')
    expect(interfaceStatusLabel(shut)).toBe('Shut')
  })

  it('names the states the MIB has that are neither up nor down', () => {
    expect(interfaceStatusLabel(link({ operStatus: 'Dormant' }))).toBe('Dormant')
    expect(interfaceTone(link({ operStatus: 'Dormant' }))).toContain('amber')
    expect(interfaceStatusLabel(link({ operStatus: 'NotPresent' }))).toBe('Not present')
  })
})
