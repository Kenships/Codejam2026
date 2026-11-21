import { useEffect, useState, type CSSProperties } from 'react'
import { useNavigate, useParams } from 'react-router'

import PixelBlast from '@/components/PixelBlast'
import { DrawingCanvasCard } from '@/components/DrawingCanvasCard'
import { LobbyPfpCanvas } from '@/components/LobbyPfpCanvas'
import {
  PlayingBoardCard,
  type IncomingRoundDrawing,
} from '@/components/PlayingBoardCard'
import { RoundResultsCard } from '@/components/RoundResultsCard'
import { SquareHueSlider, hueToHex } from '@/components/SquareHueSlider'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { createMockCompressedDrawing } from '@/lib/drawingCodec'
import { normalizeRoomCode, validateName, validateRoomCode } from '@/lib/protocol'
import { DEV_MOCK_MODE, relayClient, RelayJoinError } from '@/lib/relayClient'
import type { PlayerRoundResult } from '@/lib/results'
import { useLoginStore, type ConnectionStatus } from '@/store/useLoginStore'

export type GamePhase =
  | 'waiting'
  | 'drawing'
  | 'drawing_done'
  | 'playing'
  | 'playing_done'
  | 'results'
  | 'finished'

const DEV_GAME_PHASES: GamePhase[] = [
  'waiting',
  'drawing',
  'drawing_done',
  'playing',
  'playing_done',
  'results',
  'finished',
]
const DEV_CONNECTION_STATUSES: ConnectionStatus[] = [
  'joined',
  'connecting',
  'reconnecting',
  'host_lost',
  'disconnected',
]

export function PlayPage() {
  const { roomCode } = useParams<{ roomCode: string }>()
  const navigate = useNavigate()

  const username = useLoginStore((s) => s.username)
  const storedRoom = useLoginStore((s) => s.room)
  const status = useLoginStore((s) => s.status)
  const disconnectReason = useLoginStore((s) => s.disconnectReason)
  const setConnectionStatus = useLoginStore((s) => s.setConnectionStatus)

  // TODO: replace mock default with live `results` payload from relayClient.onMessage
  const [roundResult, setRoundResult] = useState<PlayerRoundResult | null>(
    DEV_MOCK_MODE
      ? {
          roundPoints: 240,
          totalPoints: 690,
          roundRank: 2,
          overallRank: 4,
          totalPlayers: 5,
          targetHex: '#FF5733',
        }
      : null
  )

  const room = roomCode ? roomCode.trim().toUpperCase() : storedRoom

  // Rejoin automatically on page refresh or direct /play/:roomCode navigation
  useEffect(() => {
    const roomResult = DEV_MOCK_MODE
      ? normalizeRoomCode(room)
        ? { ok: true as const, room: normalizeRoomCode(room) }
        : { ok: false as const, error: 'Missing room code.' }
      : validateRoomCode(room)
    if (!roomResult.ok) {
      navigate('/join', { replace: true })
      return
    }

    if (relayClient.isJoinedTo(roomResult.room)) {
      return
    }

    const {
      username: savedName,
      playerId,
      sessionRoom,
      setRoom,
      setConnectionStatus,
    } = useLoginStore.getState()

    const nameResult = validateName(savedName)
    if (!nameResult.ok) {
      setRoom(roomResult.room)
      navigate('/join', { replace: true })
      return
    }

    let cancelled = false
    const existingPlayerId =
      sessionRoom === roomResult.room && playerId ? playerId : undefined

    relayClient
      .join({
        room: roomResult.room,
        name: nameResult.name,
        playerId: existingPlayerId,
      })
      .catch((err: unknown) => {
        if (cancelled) return
        const message =
          err instanceof RelayJoinError
            ? err.message
            : 'Could not reconnect to the room.'
        setConnectionStatus('disconnected', message)
      })

    return () => {
      cancelled = true
    }
  }, [room, navigate])

  const [gamePhase, setGamePhase] = useState<GamePhase>('waiting')
  // Spinning rainbow hue for the waiting lobby screen
  const [waitingHue, setWaitingHue] = useState(0)
  // Picked guess hue for the playing board (null = default #ffffff white)
  const [guessHue, setGuessHue] = useState<number | null>(null)
  // Target colour received from the host (`your_target` message); mocked in DEV_MOCK_MODE
  const [drawingTargetHex, setDrawingTargetHex] = useState<string>(
    DEV_MOCK_MODE ? '#FF5733' : '#ffffff'
  )
  // Compressed drawing received from the relay/host for the `playing` (guessing) phase
  const [incomingDrawing, setIncomingDrawing] =
    useState<IncomingRoundDrawing | null>(null)

  // Seed a real GZIP-compressed mock drawing in DEV_MOCK_MODE so `playing` can be tested immediately
  useEffect(() => {
    if (!DEV_MOCK_MODE) return
    let cancelled = false
    createMockCompressedDrawing()
      .then((compressed) => {
        if (cancelled) return
        setIncomingDrawing((prev) =>
          prev ?? {
            imageBase64: compressed.base64,
            mime: compressed.mime,
            prompt: 'Golden hour sunset',
            artistName: 'Prism',
          }
        )
      })
      .catch(() => {
        // Ignore mock drawing generation error
      })
    return () => {
      cancelled = true
    }
  }, [])

  useEffect(() => {
    return relayClient.onMessage(({ d }) => {
      if (d.t === 'your_target' && typeof d.hex === 'string') {
        setDrawingTargetHex(d.hex)
      } else if (
        (d.t === 'round_drawing' || d.t === 'drawing_submit') &&
        typeof d.image === 'string'
      ) {
        setGuessHue(null)
        setIncomingDrawing({
          imageBase64: d.image,
          mime: d.mime === 'image/jpeg' ? 'image/jpeg' : 'image/png',
          prompt: typeof d.prompt === 'string' ? d.prompt : '',
          artistName: typeof d.artistName === 'string' ? d.artistName : undefined,
        })
        setGamePhase('playing')
      } else if (d.t === 'results') {
        // When results are revealed on the host screen, update roundResult and jump to results phase
        if (typeof d.targetHex === 'string' || typeof d.hex === 'string') {
          setRoundResult((prev) => ({
            roundPoints:
              typeof d.roundPoints === 'number'
                ? d.roundPoints
                : (prev?.roundPoints ?? 0),
            totalPoints:
              typeof d.totalPoints === 'number'
                ? d.totalPoints
                : (prev?.totalPoints ?? 0),
            roundRank:
              typeof d.roundRank === 'number'
                ? d.roundRank
                : (prev?.roundRank ?? 1),
            overallRank:
              typeof d.overallRank === 'number'
                ? d.overallRank
                : (prev?.overallRank ?? 1),
            totalPlayers:
              typeof d.totalPlayers === 'number'
                ? d.totalPlayers
                : prev?.totalPlayers,
            targetHex:
              typeof d.targetHex === 'string'
                ? d.targetHex
                : typeof d.hex === 'string'
                  ? d.hex
                  : (prev?.targetHex ?? '#ffffff'),
          }))
        }
        setGamePhase('results')
      } else if (
        d.t === 'state' &&
        (d.phase === 'waiting' ||
          d.phase === 'playing' ||
          d.phase === 'drawing' ||
          d.phase === 'results' ||
          d.phase === 'finished')
      ) {
        if (d.phase === 'playing') {
          setGuessHue(null)
        }
        setGamePhase(d.phase)
      }
    })
  }, [])

  // Any status other than 'joined' always falls back to the waiting/loading screen
  const activePhase: GamePhase = status !== 'joined' ? 'waiting' : gamePhase

  // Slowly cycle the hue around the rainbow on the waiting, drawing_done, and finished screens
  useEffect(() => {
    if (
      activePhase !== 'waiting' &&
      activePhase !== 'drawing_done' &&
      activePhase !== 'finished'
    )
      return

    let frameId = 0
    let lastTime = performance.now()
    const degreesPerSecond = 24 // ~15s per full rainbow cycle

    const tick = (now: number) => {
      const dt = (now - lastTime) / 1000
      lastTime = now
      setWaitingHue((prev) => (prev + dt * degreesPerSecond) % 360)
      frameId = requestAnimationFrame(tick)
    }

    frameId = requestAnimationFrame(tick)
    return () => cancelAnimationFrame(frameId)
  }, [activePhase])

  const waitingColor = hueToHex(waitingHue)
  const pickedColor = guessHue === null ? '#ffffff' : hueToHex(guessHue)
  const uiColor =
    activePhase === 'waiting' ||
    activePhase === 'drawing_done' ||
    activePhase === 'finished'
      ? waitingColor
      : activePhase === 'results' && roundResult
        ? roundResult.targetHex
        : activePhase === 'drawing'
          ? drawingTargetHex
          : pickedColor

  const handleLeave = () => {
    relayClient.disconnect()
    navigate('/join')
  }

  const handlePlayNewGame = () => {
    relayClient.disconnect()
    const { clearSession, setRoom } = useLoginStore.getState()
    clearSession()
    setRoom('')
    navigate('/join')
  }

  const renderWaitingTitle = () => {
    switch (status) {
      case 'connecting':
        return 'Joining room...'
      case 'reconnecting':
        return 'Reconnecting...'
      case 'host_lost':
        return 'Host disconnected...'
      case 'disconnected':
        return 'Disconnected'
      default:
        return 'Waiting for players...'
    }
  }

  const renderWaitingDescription = () => {
    switch (status) {
      case 'connecting':
        return 'Connecting to the relay server'
      case 'reconnecting':
        return 'Trying to restore your seat'
      case 'host_lost':
        return 'Waiting for the host to return'
      case 'disconnected':
        return disconnectReason ?? 'You are no longer connected to this room.'
      default:
        return 'The game will start once the host has enough players. Share the room code with friends to join!'
    }
  }

  const renderPhaseContent = () => {
    switch (activePhase) {
      case 'waiting':
        return (
          <SquareHueSlider value={waitingHue} onChange={setWaitingHue}>
            <Card className="w-full select-none bg-black/80 ring-0 border-0 font-jersey">
              <div className="flex flex-col gap-2 p-2">
                <CardHeader>
                  <CardTitle className="font-jersey sm:text-3xl text-2xl select-none text-center text-(--hue) [text-shadow:0_0_8px_#000,0_0_16px_#000]">
                    {renderWaitingTitle()}
                  </CardTitle>
                  <CardDescription className="font-jersey select-none text-center sm:text-base text-sm text-(--hue)/80 [text-shadow:0_0_6px_#000,0_0_12px_#000]">
                    {renderWaitingDescription()}
                  </CardDescription>
                </CardHeader>
                <CardContent className="flex flex-col items-center gap-2 pb-4 text-center text-(--hue)">
                  <div className="flex flex-wrap items-center justify-center gap-x-4 gap-y-1">
                    {room && (
                      <p className="text-base sm:text-lg tracking-wider uppercase">
                        Room: <span className="font-semibold">{room}</span>
                      </p>
                    )}
                    {username && (
                      <p className="text-base text-(--hue)/80">
                        Joined as {username}
                      </p>
                    )}
                  </div>
                  {status !== 'disconnected' && (
                    <LobbyPfpCanvas inkColor={waitingColor} />
                  )}
                  {status === 'disconnected' && (
                    <Button
                      type="button"
                      onClick={handleLeave}
                      className="mt-2 transition-none select-none bg-alpha text-(--hue) border-2 border-(--hue) font-jersey px-4 py-2 hover:bg-(--hue) hover:text-black"
                    >
                      Back to Join
                    </Button>
                  )}
                </CardContent>
              </div>
            </Card>
          </SquareHueSlider>
        )

      case 'playing':
      case 'playing_done':
        return (
          <PlayingBoardCard
            hue={guessHue}
            onHueChange={setGuessHue}
            onResetHue={() => setGuessHue(null)}
            drawing={incomingDrawing}
            forceSubmitted={activePhase === 'playing_done'}
          />
        )

      case 'drawing':
        return (
          <DrawingCanvasCard
            targetHex={drawingTargetHex}
            durationSec={60}
            onSubmit={(sub) => {
              // Feed the compressed drawing into local state and transition to the spinning waiting state
              setIncomingDrawing({
                imageBase64: sub.imageBase64,
                mime: sub.mime,
                prompt: sub.prompt,
                artistName: username || 'You',
              })
              setGamePhase('drawing_done')
            }}
          />
        )

      case 'drawing_done':
        return (
          <SquareHueSlider value={waitingHue} onChange={setWaitingHue}>
            <Card className="w-full select-none bg-black/80 ring-0 border-0 font-jersey">
              <div className="flex flex-col gap-2 p-2">
                <CardHeader>
                  <CardTitle className="font-jersey sm:text-3xl text-2xl select-none text-center text-(--hue) [text-shadow:0_0_8px_#000,0_0_16px_#000]">
                    Drawing Submitted!
                  </CardTitle>
                  <CardDescription className="font-jersey select-none text-center sm:text-base text-sm text-(--hue)/80 [text-shadow:0_0_6px_#000,0_0_12px_#000]">
                    Waiting for other players to finish drawing...
                  </CardDescription>
                </CardHeader>
                <CardContent className="flex flex-col items-center gap-2 pb-4 text-center text-(--hue)">
                  <div className="flex flex-wrap items-center justify-center gap-x-4 gap-y-1">
                    {room && (
                      <p className="text-base sm:text-lg tracking-wider uppercase">
                        Room: <span className="font-semibold">{room}</span>
                      </p>
                    )}
                    {username && (
                      <p className="text-base text-(--hue)/80">
                        Joined as {username}
                      </p>
                    )}
                  </div>
                </CardContent>
              </div>
            </Card>
          </SquareHueSlider>
        )

      case 'results':
        return roundResult ? (
          <RoundResultsCard
            username={username}
            result={roundResult}
            pickedHex={pickedColor}
          />
        ) : null

      case 'finished':
        return (
          <SquareHueSlider value={waitingHue} onChange={setWaitingHue}>
            <Card className="w-full select-none bg-black/80 ring-0 border-0 font-jersey">
              <div className="flex flex-col gap-2 p-2">
                <CardHeader>
                  <CardTitle className="font-jersey sm:text-3xl text-2xl select-none text-center text-(--hue) [text-shadow:0_0_8px_#000,0_0_16px_#000]">
                    Thanks for Playing!
                  </CardTitle>
                  <CardDescription className="font-jersey select-none text-center sm:text-base text-sm text-(--hue)/80 [text-shadow:0_0_6px_#000,0_0_12px_#000]">
                    Waiting for host to start another round...
                  </CardDescription>
                </CardHeader>
                <CardContent className="flex flex-col items-center gap-3 pb-4 text-center text-(--hue)">
                  <div className="flex flex-wrap items-center justify-center gap-x-4 gap-y-1">
                    {room && (
                      <p className="text-base sm:text-lg tracking-wider uppercase">
                        Room: <span className="font-semibold">{room}</span>
                      </p>
                    )}
                    {username && (
                      <p className="text-base text-(--hue)/80">
                        Played as {username}
                      </p>
                    )}
                  </div>

                  <Button
                    type="button"
                    onClick={handlePlayNewGame}
                    className="mt-1 transition-none select-none bg-alpha text-(--hue) border-2 border-(--hue) font-jersey px-4 py-2 hover:bg-(--hue) hover:text-black"
                  >
                    Play a New Game
                  </Button>
                </CardContent>
              </div>
            </Card>
          </SquareHueSlider>
        )
    }
  }

  return (
    <div
      className="fixed inset-0 h-dvh overflow-hidden overscroll-none touch-none bg-black flex items-center justify-center px-4 py-4 sm:py-8"
      style={{ '--hue': uiColor } as CSSProperties}
    >
      <div className="absolute inset-0">
        <PixelBlast
          variant="square"
          pixelSize={10}
          color="#000000"
          accentColor={uiColor}
          patternDensity={4}
          enableRipples={true}
          glow={0.5}
          drift={0.1}
          hoverRadius={50}
          hoverEffect="reveal"
          rippleThickness={0.4}
        />
      </div>

      <div className="relative flex flex-col items-center gap-4 w-full max-w-100">
        <div className="flex flex-col items-center select-none">
          <h1
            className={`w-max pl-[0.05em] font-jersey font-semibold tracking-wider uppercase text-center text-(--hue) [text-shadow:0_0_10px_#000,0_0_20px_#000,0_0_32px_#000] ${
              activePhase === 'drawing' ||
              activePhase === 'playing' ||
              activePhase === 'playing_done'
                ? 'text-4xl sm:text-5xl'
                : 'text-6xl sm:text-8xl'
            }`}
          >
            HueSlider
          </h1>
        </div>

        <div className="w-full">{renderPhaseContent()}</div>
      </div>

      {/* TODO: delete dev state-switcher buttons once backend is ready */}
      {DEV_MOCK_MODE && (
        <div className="fixed bottom-3 left-1/2 -translate-x-1/2 z-50 flex flex-col gap-1.5 rounded border border-white/25 bg-black/85 px-3 py-2 text-xs text-white backdrop-blur-sm max-w-[95vw]">
          <div className="flex flex-wrap items-center justify-center gap-1">
            <span className="mr-1 uppercase text-white/60">Phase:</span>
            {DEV_GAME_PHASES.map((phase) => (
              <button
                key={phase}
                type="button"
                onClick={() => {
                  if (status !== 'joined') {
                    setConnectionStatus('joined', null)
                  }
                  setGamePhase(phase)
                }}
                className={`rounded border px-2 py-0.5 cursor-pointer ${
                  activePhase === phase
                    ? 'border-(--hue) bg-(--hue) text-black font-semibold'
                    : 'border-white/30 text-white/80 hover:border-white'
                }`}
              >
                {phase}
              </button>
            ))}
          </div>
          <div className="flex flex-wrap items-center justify-center gap-1">
            <span className="mr-1 uppercase text-white/60">Status:</span>
            {DEV_CONNECTION_STATUSES.map((s) => (
              <button
                key={s}
                type="button"
                onClick={() => {
                  if (s !== 'joined') {
                    setGamePhase('waiting')
                  }
                  setConnectionStatus(
                    s,
                    s === 'disconnected' ? 'Simulated disconnect (dev)' : null
                  )
                }}
                className={`rounded border px-2 py-0.5 cursor-pointer ${
                  status === s
                    ? 'border-(--hue) bg-(--hue) text-black font-semibold'
                    : 'border-white/30 text-white/80 hover:border-white'
                }`}
              >
                {s}
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}
