import {
  CloseCode,
  MAX_FRAME_BYTES,
  formatProtocolError,
  formatRoomClosedReason,
  parseServerMessage,
  type ErrorCode,
  type JoinedMessage,
  type JoinMessage,
  type MsgMessage,
  type PlayerClientMessage,
  type SendMessage,
  type Target,
} from '@/lib/protocol'
import { useLoginStore } from '@/store/useLoginStore'

const DEFAULT_LOCAL_RELAY_URL = 'ws://localhost:8080/'
const JOIN_TIMEOUT_MS = 10_000
const RECONNECT_BASE_MS = 500
const RECONNECT_MAX_MS = 5_000

/**
 * Fake the relay entirely (no WebSocket) so the UI can be clicked through offline.
 * Opt-in with VITE_DEV_MOCK=true in frontend/.env.local, and ONLY under `npm run dev`:
 * a production build (`npm run build`) can never ship in mock mode by accident.
 */
export const DEV_MOCK_MODE =
  import.meta.env.DEV && import.meta.env.VITE_DEV_MOCK === 'true'

/** sessionStorage key for a relay URL passed as ?ws=... (the Cloudflare Tunnel backup). */
const RELAY_OVERRIDE_KEY = 'hueslider.relayUrl'

/**
 * Capture `?ws=wss://...` from the address bar and remember it for this tab.
 * Runs once at startup, before the router redirects (which can drop the query),
 * and is stored so it survives navigating from /join to /play/:roomCode.
 * `?ws=` with an empty value forgets a previous override.
 */
function captureRelayOverride(): void {
  if (typeof window === 'undefined') return
  try {
    const value = new URLSearchParams(window.location.search).get('ws')
    if (value === null) return
    const trimmed = value.trim()
    if (trimmed === '') {
      window.sessionStorage.removeItem(RELAY_OVERRIDE_KEY)
    } else if (/^wss?:\/\//i.test(trimmed)) {
      window.sessionStorage.setItem(RELAY_OVERRIDE_KEY, trimmed)
    } else {
      console.warn(`[relayClient] Ignoring ?ws=${trimmed}: must start with ws:// or wss://`)
    }
  } catch {
    // sessionStorage can be unavailable (private mode, blocked storage): just skip the override
  }
}

function getRelayOverride(): string | null {
  try {
    return typeof window === 'undefined'
      ? null
      : window.sessionStorage.getItem(RELAY_OVERRIDE_KEY)
  } catch {
    return null
  }
}

captureRelayOverride()

export class RelayJoinError extends Error {
  readonly code?: ErrorCode

  constructor(message: string, code?: ErrorCode) {
    super(message)
    this.name = 'RelayJoinError'
    this.code = code
  }
}

/** Relay URL, in priority order: ?ws= override (this tab) → VITE_RELAY_URL → local dev relay. */
export function getRelayUrl(): string {
  const configured = import.meta.env.VITE_RELAY_URL?.trim()
  const url =
    getRelayOverride() ??
    (configured && configured.length > 0 ? configured : DEFAULT_LOCAL_RELAY_URL)

  if (
    typeof window !== 'undefined' &&
    window.location.protocol === 'https:' &&
    url.startsWith('ws://')
  ) {
    throw new RelayJoinError(
      'Relay URL must use wss:// when the app is served over HTTPS.'
    )
  }

  return url
}

type MsgListener = (msg: MsgMessage) => void
type ErrorListener = (code: ErrorCode, message: string) => void

class RelayClient {
  private ws: WebSocket | null = null
  private currentRoom: string | null = null
  private joined = false
  private intentionalClose = false
  private roomClosedReceived = false
  private reconnectAttempts = 0
  private reconnectTimer: number | null = null
  private msgListeners = new Set<MsgListener>()
  private errorListeners = new Set<ErrorListener>()

  isJoinedTo(room: string): boolean {
    if (DEV_MOCK_MODE) {
      return this.joined && this.currentRoom === room
    }
    return (
      this.joined &&
      this.currentRoom === room &&
      this.ws !== null &&
      this.ws.readyState === WebSocket.OPEN
    )
  }

  onMessage(listener: MsgListener): () => void {
    this.msgListeners.add(listener)
    return () => this.msgListeners.delete(listener)
  }

  onError(listener: ErrorListener): () => void {
    this.errorListeners.add(listener)
    return () => this.errorListeners.delete(listener)
  }

  send(to: Target, d: Record<string, unknown>): boolean {
    const payload: SendMessage = { t: 'send', to, d }
    const raw = JSON.stringify(payload)
    const byteLength = new TextEncoder().encode(raw).byteLength

    if (byteLength > MAX_FRAME_BYTES) {
      console.error(
        `[relayClient] Dropped oversized frame (${byteLength} bytes > ${MAX_FRAME_BYTES} max)`
      )
      return false
    }

    if (!this.ws || this.ws.readyState !== WebSocket.OPEN || !this.joined) {
      return false
    }
    this.ws.send(raw)
    return true
  }

  disconnect(): void {
    this.clearReconnectTimer()
    this.intentionalClose = true
    this.joined = false
    this.currentRoom = null
    this.roomClosedReceived = false
    this.reconnectAttempts = 0

    if (this.ws) {
      const socket = this.ws
      this.ws = null
      socket.onopen = null
      socket.onmessage = null
      socket.onerror = null
      socket.onclose = null
      if (
        socket.readyState === WebSocket.OPEN ||
        socket.readyState === WebSocket.CONNECTING
      ) {
        socket.close(CloseCode.Normal)
      }
    }

    useLoginStore.getState().setConnectionStatus('idle', null)
  }

  join(params: {
    room: string
    name: string
    playerId?: string | null
  }): Promise<JoinedMessage> {
    if (DEV_MOCK_MODE) {
      this.clearReconnectTimer()
      this.teardownSocketOnly()
      const playerId =
        params.playerId || `p_dev_${Math.random().toString(36).slice(2, 8)}`
      const joinedMsg: JoinedMessage = {
        t: 'joined',
        room: params.room,
        playerId,
        name: params.name,
        rejoined: Boolean(params.playerId),
      }
      this.joined = true
      this.currentRoom = joinedMsg.room
      useLoginStore.getState().setJoined({
        room: joinedMsg.room,
        playerId: joinedMsg.playerId,
        name: joinedMsg.name,
      })
      return Promise.resolve(joinedMsg)
    }

    this.clearReconnectTimer()
    this.teardownSocketOnly()

    const url = getRelayUrl()
    const isReconnecting = useLoginStore.getState().status === 'reconnecting'
    if (!isReconnecting) {
      useLoginStore.getState().setConnectionStatus('connecting', null)
    }

    this.intentionalClose = false
    this.roomClosedReceived = false
    this.joined = false
    this.currentRoom = params.room

    return new Promise<JoinedMessage>((resolve, reject) => {
      let settled = false
      const settleResolve = (msg: JoinedMessage) => {
        if (settled) return
        settled = true
        window.clearTimeout(timeoutId)
        resolve(msg)
      }
      const settleReject = (err: RelayJoinError) => {
        if (settled) return
        settled = true
        window.clearTimeout(timeoutId)
        reject(err)
      }

      const ws = new WebSocket(url)
      this.ws = ws

      const timeoutId = window.setTimeout(() => {
        if (this.ws === ws && !this.joined) {
          this.teardownSocketOnly()
          useLoginStore
            .getState()
            .setConnectionStatus('disconnected', 'Timed out connecting to the game server.')
          settleReject(new RelayJoinError('Timed out connecting to the game server.'))
        }
      }, JOIN_TIMEOUT_MS)

      ws.onopen = () => {
        if (this.ws !== ws) return
        const joinMsg: JoinMessage = {
          t: 'join',
          room: params.room,
          name: params.name,
        }
        // Only include playerId when non-empty; relay rejects null/non-string playerId
        if (params.playerId) {
          joinMsg.playerId = params.playerId
        }
        this.sendRaw(joinMsg)
      }

      ws.onmessage = (event) => {
        if (this.ws !== ws) return
        if (typeof event.data !== 'string') return

        const msg = parseServerMessage(event.data)
        if (!msg) return

        switch (msg.t) {
          case 'joined': {
            this.joined = true
            this.currentRoom = msg.room
            this.reconnectAttempts = 0
            useLoginStore.getState().setJoined({
              room: msg.room,
              playerId: msg.playerId,
              name: msg.name,
            })
            // Re-send saved PFP automatically when joining/replaying in the lobby
            const savedPfp = useLoginStore.getState().pfpCompressed
            if (savedPfp) {
              this.send('host', {
                t: 'pfp_submit',
                image: savedPfp.base64,
                mime: savedPfp.mime,
                compression: 'gzip',
                w: savedPfp.width,
                h: savedPfp.height,
              })
            }
            settleResolve(msg)
            break
          }

          case 'msg': {
            for (const listener of this.msgListeners) {
              listener(msg)
            }
            break
          }

          case 'host_lost': {
            useLoginStore.getState().setConnectionStatus('host_lost', null)
            break
          }

          case 'host_back': {
            useLoginStore.getState().setConnectionStatus('joined', null)
            break
          }

          case 'room_closed': {
            this.roomClosedReceived = true
            this.joined = false
            const reason = formatRoomClosedReason(msg.reason)
            useLoginStore.getState().clearSession()
            useLoginStore.getState().setConnectionStatus('disconnected', reason)
            if (!settled) {
              this.teardownSocketOnly()
              settleReject(new RelayJoinError(reason))
            }
            break
          }

          case 'error': {
            const friendly = formatProtocolError(msg.code, msg.message)
            if (!this.joined) {
              // Join was rejected by the relay; close cleanly so the user can retry.
              this.teardownSocketOnly()
              useLoginStore.getState().setConnectionStatus('idle', null)
              settleReject(new RelayJoinError(friendly, msg.code))
            } else {
              for (const listener of this.errorListeners) {
                listener(msg.code, friendly)
              }
            }
            break
          }
        }
      }

      ws.onerror = () => {
        // The browser always follows `error` with `close`; handled in `onclose`.
      }

      ws.onclose = (event) => {
        if (this.ws !== ws) return
        this.ws = null

        const wasJoined = this.joined
        this.joined = false

        if (this.intentionalClose) {
          return
        }

        // If we hadn't completed the initial join handshake yet, surface the failure.
        if (!wasJoined) {
          const reason =
            event.code === CloseCode.PolicyViolation
              ? 'Connection rejected by server policy.'
              : 'Could not connect to the game server.'
          useLoginStore.getState().setConnectionStatus('idle', null)
          settleReject(new RelayJoinError(reason))
          return
        }

        // PROTOCOL.md §8: Reconnect on close unless code is 4000 (kicked), 4002 (replaced),
        // or a room_closed message was received.
        if (this.roomClosedReceived) {
          return
        }

        if (event.code === CloseCode.Kicked) {
          useLoginStore.getState().clearSession()
          useLoginStore
            .getState()
            .setConnectionStatus('disconnected', 'You were kicked by the host.')
          return
        }

        if (event.code === CloseCode.Replaced) {
          useLoginStore
            .getState()
            .setConnectionStatus(
              'disconnected',
              'You joined this room from another tab or device.'
            )
          return
        }

        this.scheduleReconnect()
      }
    })
  }

  private scheduleReconnect(): void {
    const { room, sessionRoom, username, playerId } = useLoginStore.getState()
    const targetRoom = sessionRoom ?? room
    if (!targetRoom || !username) {
      useLoginStore
        .getState()
        .setConnectionStatus('disconnected', 'Connection lost.')
      return
    }

    useLoginStore.getState().setConnectionStatus('reconnecting', null)

    const delay = Math.min(
      RECONNECT_BASE_MS * 2 ** this.reconnectAttempts,
      RECONNECT_MAX_MS
    )
    this.reconnectAttempts += 1

    this.reconnectTimer = window.setTimeout(() => {
      this.reconnectTimer = null
      this.join({
        room: targetRoom,
        name: username,
        playerId: sessionRoom === targetRoom ? playerId : undefined,
      }).catch((err: unknown) => {
        if (err instanceof RelayJoinError && err.code) {
          const terminalErrors: ErrorCode[] = [
            'room_not_found',
            'room_full',
            'name_invalid',
            'name_taken',
          ]
          if (terminalErrors.includes(err.code)) {
            useLoginStore.getState().clearSession()
            useLoginStore.getState().setConnectionStatus('disconnected', err.message)
            return
          }
        }
        if (!this.intentionalClose && !this.roomClosedReceived) {
          this.scheduleReconnect()
        }
      })
    }, delay)
  }

  private sendRaw(msg: PlayerClientMessage): void {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(JSON.stringify(msg))
    }
  }

  private clearReconnectTimer(): void {
    if (this.reconnectTimer !== null) {
      window.clearTimeout(this.reconnectTimer)
      this.reconnectTimer = null
    }
  }

  private teardownSocketOnly(): void {
    if (!this.ws) return
    const socket = this.ws
    this.ws = null
    socket.onopen = null
    socket.onmessage = null
    socket.onerror = null
    socket.onclose = null
    if (
      socket.readyState === WebSocket.OPEN ||
      socket.readyState === WebSocket.CONNECTING
    ) {
      socket.close(CloseCode.Normal)
    }
  }
}

export const relayClient = new RelayClient()
