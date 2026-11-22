export const ROOM_CODE_ALPHABET = 'ABCDEFGHJKMNPQRSTUVWXYZ'
export const ROOM_CODE_LENGTH = 4
export const MAX_PLAYERS = 12
export const NAME_MAX_LENGTH = 16
/** PROTOCOL.md §1: Max frame size is 64 KB. Larger frames close the socket with code 1009. */
export const MAX_FRAME_BYTES = 64 * 1024

export const CloseCode = {
  Normal: 1000,
  GoingAway: 1001,
  PolicyViolation: 1008,
  MessageTooBig: 1009,
  Kicked: 4000,
  Replaced: 4002,
} as const

export type ErrorCode =
  | 'bad_json'
  | 'bad_message'
  | 'not_in_room'
  | 'already_in_room'
  | 'room_not_found'
  | 'room_full'
  | 'name_invalid'
  | 'name_taken'
  | 'bad_host_key'
  | 'host_active'
  | 'forbidden'
  | 'unknown_target'
  | 'rate_limited'

export type Target = 'host' | 'players' | 'all' | (string & {})

// ---- Client -> Relay (player messages) ----

export type JoinMessage = {
  t: 'join'
  room: string
  name: string
  playerId?: string
}

export type SendMessage = {
  t: 'send'
  to: Target
  d: Record<string, unknown>
}

/** Leaving on purpose: the relay frees the seat and the name at once (no 30 s grace hold). */
export type LeaveMessage = {
  t: 'leave'
}

export type PlayerClientMessage = JoinMessage | SendMessage | LeaveMessage

// ---- Relay -> Client (player-relevant messages) ----

export type JoinedMessage = {
  t: 'joined'
  room: string
  playerId: string
  name: string
  rejoined: boolean
}

export type MsgMessage = {
  t: 'msg'
  from: 'host' | (string & {})
  d: Record<string, unknown>
  catchUp?: true
  ageMs?: number
}

export type HostLostMessage = {
  t: 'host_lost'
}

export type HostBackMessage = {
  t: 'host_back'
}

export type RoomClosedReason = 'host_timeout' | 'idle' | 'server_shutdown'

export type RoomClosedMessage = {
  t: 'room_closed'
  reason: RoomClosedReason
}

export type ErrorMessage = {
  t: 'error'
  code: ErrorCode
  message?: string
}

export type PlayerServerMessage =
  | JoinedMessage
  | MsgMessage
  | HostLostMessage
  | HostBackMessage
  | RoomClosedMessage
  | ErrorMessage

const ROOM_CODE_ALLOWED_REGEX = new RegExp(`^[${ROOM_CODE_ALPHABET}]{${ROOM_CODE_LENGTH}}$`)

export function normalizeRoomCode(input: string): string {
  return input.trim().toUpperCase()
}

export function validateRoomCode(
  input: string
): { ok: true; room: string } | { ok: false; error: string } {
  const room = normalizeRoomCode(input)
  if (room.length !== ROOM_CODE_LENGTH) {
    return {
      ok: false,
      error: `Room code must be ${ROOM_CODE_LENGTH} letters.`,
    }
  }
  if (!ROOM_CODE_ALLOWED_REGEX.test(room)) {
    return {
      ok: false,
      error: 'Room code must use 4 letters (no I, L, or O).',
    }
  }
  return { ok: true, room }
}

export function validateName(
  input: string
): { ok: true; name: string } | { ok: false; error: string } {
  const name = input.trim()
  if (name.length === 0) {
    return { ok: false, error: 'Enter a username.' }
  }
  if (name.length > NAME_MAX_LENGTH) {
    return {
      ok: false,
      error: `Username must be ${NAME_MAX_LENGTH} characters or fewer.`,
    }
  }
  return { ok: true, name }
}

export function formatProtocolError(code: ErrorCode, fallback?: string): string {
  switch (code) {
    case 'room_not_found':
      return 'Room not found. Check the code and try again.'
    case 'room_full':
      // The host can set a lower limit than MAX_PLAYERS
      return 'This room is full.'
    case 'name_invalid':
      return `Username must be 1–${NAME_MAX_LENGTH} characters.`
    case 'name_taken':
      return 'That username is already taken in this room.'
    case 'rate_limited':
      return 'Too many requests. Please wait a moment.'
    case 'already_in_room':
      return 'Already connected to a room.'
    case 'not_in_room':
      return 'Not connected to a room.'
    case 'forbidden':
      return 'Action not allowed.'
    case 'unknown_target':
      return 'Unknown recipient.'
    case 'bad_json':
    case 'bad_message':
      return fallback || 'Invalid protocol message.'
    default:
      return fallback || `Connection error (${code}).`
  }
}

export function formatRoomClosedReason(reason: RoomClosedReason): string {
  switch (reason) {
    case 'host_timeout':
      return 'The host disconnected and the room closed.'
    case 'idle':
      return 'The room closed due to inactivity.'
    case 'server_shutdown':
      return 'The server is restarting. Please join again shortly.'
  }
}

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

export function parseServerMessage(raw: string): PlayerServerMessage | null {
  let parsed: unknown
  try {
    parsed = JSON.parse(raw)
  } catch {
    return null
  }
  if (!isRecord(parsed) || typeof parsed.t !== 'string') {
    return null
  }

  switch (parsed.t) {
    case 'joined':
      if (
        typeof parsed.room === 'string' &&
        typeof parsed.playerId === 'string' &&
        typeof parsed.name === 'string' &&
        typeof parsed.rejoined === 'boolean'
      ) {
        return {
          t: 'joined',
          room: parsed.room,
          playerId: parsed.playerId,
          name: parsed.name,
          rejoined: parsed.rejoined,
        }
      }
      return null

    case 'msg':
      if (typeof parsed.from === 'string' && isRecord(parsed.d)) {
        const msg: MsgMessage = { t: 'msg', from: parsed.from, d: parsed.d }
        if (parsed.catchUp === true) {
          msg.catchUp = true
        }
        if (typeof parsed.ageMs === 'number' && Number.isFinite(parsed.ageMs)) {
          msg.ageMs = parsed.ageMs
        }
        return msg
      }
      return null

    case 'host_lost':
      return { t: 'host_lost' }

    case 'host_back':
      return { t: 'host_back' }

    case 'room_closed':
      if (
        parsed.reason === 'host_timeout' ||
        parsed.reason === 'idle' ||
        parsed.reason === 'server_shutdown'
      ) {
        return { t: 'room_closed', reason: parsed.reason }
      }
      return null

    case 'error':
      if (typeof parsed.code === 'string') {
        return {
          t: 'error',
          code: parsed.code as ErrorCode,
          message: typeof parsed.message === 'string' ? parsed.message : undefined,
        }
      }
      return null

    default:
      return null
  }
}
