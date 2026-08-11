import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useEffect, useState } from 'react'
import { apiBaseUrl } from '../../api/client'
import { userManager } from '../../auth/auth'
import type { Alert, DeviceStatusTile } from '../../api/monitoring'

export type MonitoringHubEvents = {
  onAlertChanged?: (alert: Alert) => void
  onDeviceStatusChanged?: (tile: DeviceStatusTile) => void
  /**
   * Fired when the socket comes back after a drop. A push missed while disconnected is gone — the
   * hub keeps no backlog — so every board re-reads here rather than trusting that it stayed in step.
   * This is the whole reason a broadcast is allowed not to be durable.
   */
  onResync?: () => void
}

export type MonitoringHubStatus = 'connecting' | 'live' | 'reconnecting' | 'offline'

type Listener = MonitoringHubEvents

/**
 * One connection for the whole app, shared by every screen that wants live monitoring (CONVENTIONS:
 * a shared hook per hub, components never open a raw connection). It is opened when the first
 * subscriber mounts and closed when the last one leaves, so a user who never visits monitoring never
 * holds a socket.
 */
const listeners = new Set<Listener>()
let connection: HubConnection | null = null
let status: MonitoringHubStatus = 'offline'
const statusWatchers = new Set<(next: MonitoringHubStatus) => void>()

function setStatus(next: MonitoringHubStatus) {
  status = next
  for (const watcher of statusWatchers) watcher(next)
}

function emit<K extends keyof Listener>(event: K, ...args: Parameters<NonNullable<Listener[K]>>) {
  for (const listener of listeners) {
    const handler = listener[event] as ((...values: unknown[]) => void) | undefined
    handler?.(...args)
  }
}

function open() {
  if (connection) return connection

  const built = new HubConnectionBuilder()
    .withUrl(`${apiBaseUrl}/hubs/monitoring`, {
      // The token is fetched per (re)connection rather than captured once, so a connection that
      // drops and comes back after a silent renew presents the new token rather than an expired one.
      accessTokenFactory: async () => (await userManager.getUser())?.access_token ?? '',
    })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()

  built.on('AlertChanged', (alert: Alert) => emit('onAlertChanged', alert))
  built.on('DeviceStatusChanged', (tile: DeviceStatusTile) => emit('onDeviceStatusChanged', tile))
  built.onreconnecting(() => setStatus('reconnecting'))
  built.onreconnected(() => {
    setStatus('live')
    emit('onResync')
  })
  built.onclose(() => setStatus('offline'))

  connection = built
  setStatus('connecting')
  built.start()
    .then(() => setStatus('live'))
    // A board that cannot open a socket is a board that has to be refreshed by hand, not a broken
    // page: every screen here reads its data over HTTP first and only then listens for changes.
    .catch(() => setStatus('offline'))
  return built
}

function close() {
  const current = connection
  connection = null
  setStatus('offline')
  void current?.stop()
}

export function useMonitoringHub(events: MonitoringHubEvents): MonitoringHubStatus {
  const [current, setCurrent] = useState<MonitoringHubStatus>(status)

  useEffect(() => {
    statusWatchers.add(setCurrent)
    return () => { statusWatchers.delete(setCurrent) }
  }, [])

  useEffect(() => {
    const listener: Listener = events
    listeners.add(listener)
    open()
    setCurrent(status)

    return () => {
      listeners.delete(listener)
      if (listeners.size === 0) close()
    }
    // Re-registered whenever a handler identity changes; callers pass stable callbacks.
  }, [events])

  return current
}

/** Test seam: forgets the shared connection so one test's socket cannot leak into the next. */
export function resetMonitoringHubForTests() {
  listeners.clear()
  statusWatchers.clear()
  const current = connection
  connection = null
  status = 'offline'
  void current?.stop()
}

export function monitoringHubState() {
  return connection?.state ?? HubConnectionState.Disconnected
}
