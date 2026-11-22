import { create } from 'zustand'
import { persist } from 'zustand/middleware'
import type { DrawingMime, DrawingStroke } from '@/lib/drawingCodec'

export type ConnectionStatus =
  | 'idle'
  | 'connecting'
  | 'joined'
  | 'reconnecting'
  | 'host_lost'
  | 'disconnected'

export interface SavedPfpCompressed {
  base64: string
  mime: DrawingMime
  width: number
  height: number
}

export interface LoginState {
  // Persisted identity & PFP (PROTOCOL.md §2 & §8)
  username: string
  room: string
  playerId: string | null
  /** Room code associated with the saved playerId, so we only send playerId when rejoining the same room. */
  sessionRoom: string | null
  /** Name the saved seat was joined under. A rejoin keeps the seat's name, so a different name means a new seat. */
  sessionName: string | null
  /** Player-drawn profile picture data URL */
  pfpDataUrl: string | null
  /** Normalized strokes of the player's PFP so it reloads into the lobby canvas across replays */
  pfpStrokes: DrawingStroke[]
  /** GZIP-compressed PFP payload ready to re-send on lobby replay or reconnect */
  pfpCompressed: SavedPfpCompressed | null

  // Ephemeral connection state (not persisted)
  status: ConnectionStatus
  disconnectReason: string | null

  setUsername: (username: string) => void
  setRoom: (room: string) => void
  setPfpDataUrl: (pfpDataUrl: string | null) => void
  setPfp: (
    pfp: {
      dataUrl: string
      strokes: DrawingStroke[]
      compressed: SavedPfpCompressed
    } | null
  ) => void
  setJoined: (data: { room: string; playerId: string; name: string }) => void
  setConnectionStatus: (
    status: ConnectionStatus,
    disconnectReason?: string | null
  ) => void
  clearSession: () => void
  reset: () => void
}

const persistedInitialState = {
  username: '',
  room: '',
  playerId: null as string | null,
  sessionRoom: null as string | null,
  sessionName: null as string | null,
  pfpDataUrl: null as string | null,
  pfpStrokes: [] as DrawingStroke[],
  pfpCompressed: null as SavedPfpCompressed | null,
}

const ephemeralInitialState = {
  status: 'idle' as ConnectionStatus,
  disconnectReason: null as string | null,
}

export const useLoginStore = create<LoginState>()(
  persist(
    (set) => ({
      ...persistedInitialState,
      ...ephemeralInitialState,
      setUsername: (username) => set({ username }),
      setRoom: (room) => set({ room }),
      setPfpDataUrl: (pfpDataUrl) => set({ pfpDataUrl }),
      setPfp: (pfp) =>
        set(
          pfp
            ? {
                pfpDataUrl: pfp.dataUrl,
                pfpStrokes: pfp.strokes,
                pfpCompressed: pfp.compressed,
              }
            : {
                pfpDataUrl: null,
                pfpStrokes: [],
                pfpCompressed: null,
              }
        ),
      setJoined: ({ room, playerId, name }) =>
        set({
          room,
          sessionRoom: room,
          sessionName: name,
          playerId,
          username: name,
          status: 'joined',
          disconnectReason: null,
        }),
      setConnectionStatus: (status, disconnectReason = null) =>
        set({ status, disconnectReason }),
      clearSession: () =>
        set({
          playerId: null,
          sessionRoom: null,
          sessionName: null,
        }),
      reset: () =>
        set({
          ...persistedInitialState,
          ...ephemeralInitialState,
        }),
    }),
    {
      name: 'hueslider-login',
      partialize: (state) => ({
        username: state.username,
        room: state.room,
        playerId: state.playerId,
        sessionRoom: state.sessionRoom,
        sessionName: state.sessionName,
        pfpDataUrl: state.pfpDataUrl,
        pfpStrokes: state.pfpStrokes,
        pfpCompressed: state.pfpCompressed,
      }),
      merge: (persistedState, currentState) => {
        const persisted =
          typeof persistedState === 'object' && persistedState !== null
            ? (persistedState as Partial<LoginState>)
            : {}
        return {
          ...currentState,
          ...persisted,
          pfpStrokes: Array.isArray(persisted.pfpStrokes)
            ? persisted.pfpStrokes
            : [],
        }
      },
    }
  )
)
