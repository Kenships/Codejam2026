import { useEffect, useState, type FormEvent } from 'react'
import { useNavigate, useSearchParams } from 'react-router'
import { LobbyPfpCanvas } from '@/components/LobbyPfpCanvas'
import {
  Card,
  CardHeader,
  CardTitle,
  CardContent,
  CardDescription,
  CardFooter,
} from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import {
  NAME_MAX_LENGTH,
  ROOM_CODE_LENGTH,
  normalizeRoomCode,
  validateName,
  validateRoomCode,
} from '@/lib/protocol'
import { DEV_MOCK_MODE, relayClient, RelayJoinError } from '@/lib/relayClient'
import { useLoginStore } from '@/store/useLoginStore'

interface LoginCardProps {
  inkColor?: string
}

// --hue is set by LoginPage to the color picked in the hue slider
export const LoginCard = ({ inkColor = '#ffffff' }: LoginCardProps) => {
  const navigate = useNavigate()
  const username = useLoginStore((s) => s.username)
  const room = useLoginStore((s) => s.room)
  const playerId = useLoginStore((s) => s.playerId)
  const sessionRoom = useLoginStore((s) => s.sessionRoom)
  const sessionName = useLoginStore((s) => s.sessionName)
  const setUsername = useLoginStore((s) => s.setUsername)
  const setRoom = useLoginStore((s) => s.setRoom)

  const [error, setError] = useState<string | null>(null)
  const [isJoining, setIsJoining] = useState(false)

  // Pre-fill the room code from a link / QR code: /join?room=ABCD
  const [searchParams] = useSearchParams()
  useEffect(() => {
    const fromLink = searchParams.get('room')
    if (!fromLink) return
    const result = validateRoomCode(fromLink)
    if (result.ok) setRoom(result.room)
  }, [searchParams, setRoom])

  const handleSubmit = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    if (isJoining) return

    const nameResult = validateName(username)
    if (!nameResult.ok) {
      setError(nameResult.error)
      return
    }

    const roomResult = DEV_MOCK_MODE
      ? normalizeRoomCode(room)
        ? { ok: true as const, room: normalizeRoomCode(room) }
        : { ok: false as const, error: 'Enter a room code.' }
      : validateRoomCode(room)
    if (!roomResult.ok) {
      setError(roomResult.error)
      return
    }

    setError(null)
    setIsJoining(true)

    try {
      // Only send saved playerId when rejoining the same room (PROTOCOL.md §3 & §8)
      // under the same name: a rejoin keeps the seat's old name, so a new name needs a new seat.
      const sameSeat =
        sessionRoom === roomResult.room &&
        !!playerId &&
        sessionName?.toLowerCase() === nameResult.name.toLowerCase()
      const existingPlayerId = sameSeat ? playerId : undefined

      // Still seated in this room under another name: give that seat up first,
      // otherwise the relay holds it (and its name) for the 30 s grace period.
      if (!sameSeat && relayClient.isJoinedTo(roomResult.room)) {
        relayClient.leave()
      }

      const joined = await relayClient.join({
        room: roomResult.room,
        name: nameResult.name,
        playerId: existingPlayerId,
      })

      navigate(`/play/${encodeURIComponent(joined.room)}`)
    } catch (err) {
      if (err instanceof RelayJoinError) {
        setError(err.message)
      } else {
        setError('Could not join the game. Please try again.')
      }
    } finally {
      setIsJoining(false)
    }
  }

  return (
    <Card className="w-full select-none bg-black/70 ring-0 border-2 border-(--hue) font-jersey">
      <form onSubmit={handleSubmit} className="flex flex-col gap-2 p-2">
        <CardHeader>
          <CardTitle className="font-jersey sm:text-2xl text-lg select-none text-center text-(--hue) [text-shadow:0_0_8px_#000,0_0_16px_#000]">
            Join a Game
          </CardTitle>
          <CardDescription className="font-jersey select-none sm:text-center sm:text-base text-sm text-center text-(--hue) [text-shadow:0_0_6px_#000,0_0_12px_#000]">
            Enter your username and the 4-letter room code
          </CardDescription>
          {/* px-0: already inside CardHeader's padding; text-base: iOS zooms into inputs under 16px */}
          <CardContent className="flex flex-col gap-3 px-0">
            <input
              type="text"
              placeholder="Username"
              maxLength={NAME_MAX_LENGTH}
              autoComplete="nickname"
              disabled={isJoining}
              value={username}
              onChange={(e) => {
                setUsername(e.target.value)
                if (error) setError(null)
              }}
              className="border-2 border-(--hue) font-jersey bg-black/50 p-2 text-base text-(--hue) placeholder:text-(--hue)/60 disabled:opacity-60"
            />
            <input
              type="text"
              placeholder="Room Code"
              maxLength={DEV_MOCK_MODE ? undefined : ROOM_CODE_LENGTH}
              autoCapitalize="characters"
              autoCorrect="off"
              autoComplete="off"
              spellCheck={false}
              disabled={isJoining}
              value={room}
              onChange={(e) => {
                setRoom(e.target.value.toUpperCase())
                if (error) setError(null)
              }}
              className="border-2 border-(--hue) font-jersey bg-black/50 p-2 text-base uppercase tracking-widest text-(--hue) placeholder:text-(--hue)/60 placeholder:normal-case placeholder:tracking-normal disabled:opacity-60"
            />
            <LobbyPfpCanvas inkColor={inkColor} />
            {error && (
              <p
                role="alert"
                className="text-center text-sm sm:text-base text-red-400 [text-shadow:0_0_6px_#000]"
              >
                {error}
              </p>
            )}
          </CardContent>
          <CardFooter className="flex justify-center gap-4">
            <Button
              type="submit"
              disabled={isJoining}
              className="transition-none select-none bg-alpha text-(--hue) border-2 border-(--hue) font-jersey px-4 py-2 hover:bg-(--hue) hover:text-black disabled:opacity-60"
            >
              {isJoining ? 'Joining...' : 'Join Game'}
            </Button>
          </CardFooter>
        </CardHeader>
      </form>
    </Card>
  )
}
