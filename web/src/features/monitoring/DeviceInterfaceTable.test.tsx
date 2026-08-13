import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { DeviceInterface } from '../../api/monitoring'
import { DeviceInterfaceTable } from './DeviceInterfaceTable'

function link(overrides: Partial<DeviceInterface> = {}): DeviceInterface {
  return {
    ifIndex: 1, name: 'Gi0/1', alias: null, macAddress: null, interfaceType: 6,
    adminStatus: 'Up', operStatus: 'Up', speedBitsPerSecond: 1_000_000_000,
    bitsInPerSecond: null, bitsOutPerSecond: null, utilisationPercent: null,
    errorsInPerSecond: null, errorsOutPerSecond: null,
    discardsInPerSecond: null, discardsOutPerSecond: null,
    checkId: 'check-1', metricPrefix: 'interface.1.', observedAt: new Date().toISOString(),
    ...overrides,
  }
}

function rowFor(name: string) {
  return screen.getByRole('button', { name }).closest('tr')!
}

describe('DeviceInterfaceTable', () => {
  it('lists each port with its alias, traffic and utilisation', () => {
    render(<DeviceInterfaceTable
      interfaces={[link({
        ifIndex: 1, name: 'Gi0/1', alias: 'uplink to core',
        bitsInPerSecond: 12_500_000, bitsOutPerSecond: 800, utilisationPercent: 1.25,
      })]}
      isLoading={false} selectedIfIndex={null} onSelect={vi.fn()} />)

    const row = rowFor('Gi0/1')
    expect(within(row).getByText('uplink to core')).toBeInTheDocument()
    expect(within(row).getByText('12.5 Mbit/s')).toBeInTheDocument()
    expect(within(row).getByText('800 bit/s')).toBeInTheDocument()
    expect(within(row).getByText('1.3%')).toBeInTheDocument()
    expect(within(row).getByText('Up')).toBeInTheDocument()
  })

  /**
   * The distinction the whole table rests on: a port nobody has measured twice shows a dash, and a
   * port carrying nothing shows a zero. Rendering the first as the second would make a poller that
   * has just restarted look like an estate that has gone quiet.
   */
  it('shows a dash for a rate the poller has not measured yet', () => {
    render(<DeviceInterfaceTable
      interfaces={[link({ name: 'Gi0/5' })]}
      isLoading={false} selectedIfIndex={null} onSelect={vi.fn()} />)

    expect(within(rowFor('Gi0/5')).getAllByText('—').length).toBeGreaterThan(0)
  })

  /** A shut port is not a fault, and the table says so the same way the alert rule does. */
  it('labels an administratively shut port rather than calling it down', () => {
    render(<DeviceInterfaceTable
      interfaces={[link({ name: 'Gi0/9', operStatus: 'Down', adminStatus: 'Down' })]}
      isLoading={false} selectedIfIndex={null} onSelect={vi.fn()} />)

    expect(within(rowFor('Gi0/9')).getByText('Shut')).toBeInTheDocument()
    expect(within(rowFor('Gi0/9')).queryByText('Down')).not.toBeInTheDocument()
  })

  /**
   * Selecting a port charts one of its series, which is what makes a per-interface graph an ordinary
   * series query — the metric prefix comes from the API so the browser never builds a metric name.
   */
  it('hands back the port when its row is chosen', async () => {
    const onSelect = vi.fn()
    render(<DeviceInterfaceTable
      interfaces={[link({ ifIndex: 4, name: 'Gi0/4', metricPrefix: 'interface.4.' })]}
      isLoading={false} selectedIfIndex={null} onSelect={onSelect} />)

    await userEvent.click(screen.getByRole('button', { name: 'Gi0/4' }))

    expect(onSelect).toHaveBeenCalledWith(expect.objectContaining({ ifIndex: 4, metricPrefix: 'interface.4.' }))
  })

  it('marks the chosen row as selected', () => {
    render(<DeviceInterfaceTable
      interfaces={[link({ ifIndex: 1, name: 'Gi0/1' }), link({ ifIndex: 2, name: 'Gi0/2' })]}
      isLoading={false} selectedIfIndex={2} onSelect={vi.fn()} />)

    expect(rowFor('Gi0/2')).toHaveAttribute('aria-selected', 'true')
    expect(rowFor('Gi0/1')).toHaveAttribute('aria-selected', 'false')
  })

  /** DESIGN.md §6: an empty state explains and offers a way forward, never a bare "no data". */
  it('explains how to poll interfaces when the device reports none', () => {
    render(<DeviceInterfaceTable interfaces={[]} isLoading={false} selectedIfIndex={null} onSelect={vi.fn()} />)

    expect(screen.getByText(/has not reported any interfaces/)).toBeInTheDocument()
    expect(screen.getByText('interfaces')).toBeInTheDocument()
  })

  /** DESIGN.md §10: loading is a skeleton, never a spinner inside a card. */
  it('shows a skeleton while loading', () => {
    render(<DeviceInterfaceTable interfaces={[]} isLoading selectedIfIndex={null} onSelect={vi.fn()} />)

    expect(screen.getByLabelText('Loading interfaces')).toBeInTheDocument()
  })
})
