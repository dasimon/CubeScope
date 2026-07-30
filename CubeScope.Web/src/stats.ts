// Connexion SignalR au hub stats : reçoit les deltas perfmon poussés après chaque requête.
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
    // Un push reçu = perfmon opérationnel, quel que soit le dernier statut chargé
    store.statsStatus = { status: 'Ready', detail: store.statsStatus?.detail ?? null }
  })

  conn.on('queryProfile', (profile: QueryProfile) => actions.setProfile(profile))

  conn.start().catch(() => {
    /* hub indisponible : les stats resteront vides, non bloquant */
  })

  // Prévient le serveur que la page s'en va, pour qu'il distingue une fermeture (ou un F5)
  // d'un transport qui lâche — sans ça, il ne peut pas savoir s'il doit s'arrêter vite ou
  // patienter le temps que le client se reconnecte. sendBeacon est le seul envoi qui
  // aboutit de façon fiable pendant le déchargement de la page.
  // `pagehide` plutôt que `beforeunload` : il couvre aussi la mise en cache arrière/avant.
  window.addEventListener('pagehide', () => {
    navigator.sendBeacon('/api/leaving')
  })
}
