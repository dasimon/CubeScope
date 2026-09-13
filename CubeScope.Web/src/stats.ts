// SignalR connection to the stats hub: receives the perfmon deltas pushed after each query.
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { actions, store } from './store'
import type { CounterDelta, QueryProfile } from './api'

export function startStatsHub(): void {
  const conn = new HubConnectionBuilder()
    .withUrl('/hubs/stats')
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()

  conn.on('queryStats', (payload: { durationMs: number; deltas: CounterDelta[] }) => {
    store.stats = payload.deltas
    store.statsQueryDurationMs = payload.durationMs
    // A push received = perfmon is working, whatever the last loaded status
    store.statsStatus = { status: 'Ready', detail: store.statsStatus?.detail ?? null }
  })

  conn.on('queryProfile', (profile: QueryProfile) => actions.setProfile(profile))

  conn.start().catch(() => {
    /* hub unavailable: stats will stay empty, non-blocking */
  })

  // Tells the server the page is leaving, so it can tell a close (or an F5) apart from
  // a dropped transport — without this, it cannot know whether to shut down quickly or
  // wait for the client to reconnect. sendBeacon is the only send that reliably gets
  // through while the page is unloading.
  // `pagehide` rather than `beforeunload`: it also covers the back/forward cache.
  window.addEventListener('pagehide', () => {
    navigator.sendBeacon('/api/leaving')
  })
}
