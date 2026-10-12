import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import type { RealtimeChange, RealtimeCoverage, RealtimeSubscription } from './types'
import { normalizeChange, normalizeCoverage, type RealtimeWireChange, type RealtimeWireCoverage } from './types'
export interface RealtimeConnection {
  start(): Promise<void>; stop(): Promise<void>;
  subscribe(requests: RealtimeSubscription[]): Promise<RealtimeCoverage[]>;
  heartbeat(): Promise<RealtimeCoverage[]>;
  unsubscribe(): Promise<void>;
  onChanged(callback: (change: RealtimeChange) => void): void;
  onSessionRevoked(callback: () => void): void;
  onAccessRevoked(callback: (tenantId?: string) => void): void;
  onReconnecting(callback: () => void): void;
  onReconnected(callback: () => void): void;
  onClose(callback: () => void): void;
}
export function createRealtimeConnection(baseUrl: string): RealtimeConnection {
  const hub = new HubConnectionBuilder().withUrl(`${baseUrl}/api/v1/realtime/hub`, { withCredentials: true })
    .withAutomaticReconnect([0, 1000, 5000, 10000]).configureLogging(LogLevel.None).build()
  return { start: () => hub.start(), stop: () => hub.stop(), subscribe: (requests) => hub.invoke<RealtimeWireCoverage[]>('Subscribe', requests).then(values => values.map(normalizeCoverage)),
    heartbeat: () => hub.invoke<RealtimeWireCoverage[]>('Heartbeat').then(values => values.map(normalizeCoverage)),
    unsubscribe: () => hub.invoke('Unsubscribe'),
    onChanged: (callback) => hub.on('Changed', (value: RealtimeWireChange) => callback(normalizeChange(value))), onSessionRevoked: (callback) => hub.on('SessionRevoked', callback),
    onAccessRevoked: (callback) => hub.on('AccessRevoked', (tenantId: string | null) => callback(tenantId ?? undefined)), onReconnecting: (callback) => hub.onreconnecting(callback),
    onReconnected: (callback) => hub.onreconnected(callback), onClose: (callback) => hub.onclose(callback) }
}
