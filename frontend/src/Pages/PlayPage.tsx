import { useEffect, useState, type CSSProperties } from 'react'
import { useNavigate, useParams } from 'react-router'

import PixelBlast from '@/components/PixelBlast'
import { CategoryPickerCard } from '@/components/CategoryPickerCard'
import { DrawingCanvasCard } from '@/components/DrawingCanvasCard'
import { HowToPlay, TutorialTabs } from '@/components/HowToPlay'
import { LobbyPfpCanvas } from '@/components/LobbyPfpCanvas'
import {
  DrawingViewport,
  PlayingBoardCard,
  type IncomingRoundDrawing,
} from '@/components/PlayingBoardCard'
import { RoundResultsCard } from '@/components/RoundResultsCard'
import { SquareHueSlider, hueToHex } from '@/components/SquareHueSlider'
import { TrueHueTitle } from '@/components/TrueHueTitle'
import { BouncingDots } from '@/components/ui/bouncing-dots'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { createMockCompressedDrawing, toDrawingMime } from '@/lib/drawingCodec'
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
  | 'results_artist'
  | 'finished'

interface DrawingTarget {
  hex: string
  /** 4 category choices from host, or null for old hosts that don't send categories */
  categories: string[] | null
  /** Picked category (from host on rejoin, or tapped locally), or null if not picked yet */
  category: string | null
}

const DEV_GAME_PHASES: GamePhase[] = [
  'waiting',
  'drawing',
  'drawing_done',
  'playing',
  'playing_done',
  'results',
  'results_artist',
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
  // Target colour & category picker state received from the host (`your_target`); mocked in DEV_MOCK_MODE
  const [drawingTarget, setDrawingTarget] = useState<DrawingTarget | null>(
    DEV_MOCK_MODE
      ? {
          hex: '#FF5733',
          categories: ['Animal', 'Food', 'Place', 'Weather'],
          category: null,
        }
      : null
  )
  // Drawing countdown deadline (derived from state.remainingMs minus catch-up ageMs)
  const [drawingDeadlineMs, setDrawingDeadlineMs] = useState<number | null>(null)
  const [drawingDurationSec, setDrawingDurationSec] = useState<number>(60)
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
    return relayClient.onMessage((msg) => {
      const { d, ageMs } = msg
      const rawRemainingMs =
        typeof d.remainingMs === 'number' && Number.isFinite(d.remainingMs)
          ? d.remainingMs
          : undefined
      const remainingMs =
        rawRemainingMs !== undefined
          ? Math.max(0, rawRemainingMs - (typeof ageMs === 'number' ? ageMs : 0))
          : undefined

      if (d.t === 'your_target' && typeof d.hex === 'string') {
        const hex = d.hex
        const categories =
          Array.isArray(d.categories) &&
          d.categories.length > 0 &&
          d.categories.every((c): c is string => typeof c === 'string')
            ? d.categories
            : null
        const category =
          typeof d.category === 'string' && d.category.length > 0
            ? d.category
            : null

        setDrawingTarget((prev) => {
          const sameTarget =
            prev !== null &&
            prev.hex === hex &&
            prev.categories !== null &&
            categories !== null &&
            prev.categories.length === categories.length &&
            prev.categories.every((c, i) => c === categories[i])
          return {
            hex,
            categories,
            category: category ?? (sameTarget ? prev.category : null),
          }
        })
      } else if (
        (d.t === 'round_drawing' || d.t === 'drawing_submit') &&
        typeof d.image === 'string'
      ) {
        setGuessHue(null)
        setIncomingDrawing({
          imageBase64: d.image,
          mime: toDrawingMime(d.mime),
          prompt: typeof d.prompt === 'string' ? d.prompt : '',
          artistName: typeof d.artistName === 'string' ? d.artistName : undefined,
        })
        setGamePhase('playing')
      } else if (d.t === 'results') {
        // When results are revealed on the host screen, update roundResult and jump to results phase
        const myPlayerId = useLoginStore.getState().playerId
        const isArtist =
          d.isArtist === true ||
          (typeof d.artistId === 'string' &&
            myPlayerId !== null &&
            d.artistId === myPlayerId)

        if (
          typeof d.targetHex === 'string' ||
          typeof d.hex === 'string' ||
          typeof d.isArtist === 'boolean' ||
          typeof d.artistId === 'string'
        ) {
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
            isArtist,
          }))
        }
        setGamePhase(isArtist ? 'results_artist' : 'results')
      } else if (
        d.t === 'state' &&
        (d.phase === 'waiting' ||
          d.phase === 'playing' ||
          d.phase === 'drawing' ||
          d.phase === 'results' ||
          d.phase === 'finished')
      ) {
        if (d.phase === 'drawing') {
          if (remainingMs !== undefined) {
            setDrawingDeadlineMs(Date.now() + remainingMs)
            if (rawRemainingMs !== undefined && rawRemainingMs > 0) {
              const totalSec = Math.max(1, Math.ceil(rawRemainingMs / 1000))
              setDrawingDurationSec((prev) => Math.max(prev, totalSec))
            }
          }
        } else {
          setDrawingTarget(null)
          setDrawingDeadlineMs(null)
          setDrawingDurationSec(60)
          if (d.phase === 'playing') {
            setGuessHue(null)
          }
        }
        if (d.phase === 'drawing' && typeof d.remainingMs === 'number') {
          // A catch-up replay is ageMs old; never start below 1 s
          const remainingMs = d.remainingMs - (ageMs ?? 0)
          setDrawingDurationSec(Math.max(1, Math.round(remainingMs / 1000)))
        }
        setGamePhase(d.phase)
      }
    })
  }, [])

  // Any status other than 'joined' always falls back to the waiting/loading screen
  const activePhase: GamePhase = status !== 'joined' ? 'waiting' : gamePhase
  // In phase "drawing" without `your_target` (not an artist this game, or joined mid-game): waiting screen
  const isDrawingWaiting = activePhase === 'drawing' && drawingTarget === null
  const isWaitingScreen =
    activePhase === 'waiting' ||
    activePhase === 'drawing_done' ||
    activePhase === 'finished' ||
    isDrawingWaiting

  // Slowly cycle the hue around the rainbow on waiting, drawing_done, mid-game drawing wait, and finished screens
  useEffect(() => {
    if (!isWaitingScreen) return

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
  }, [isWaitingScreen])

  const waitingColor = hueToHex(waitingHue)
  const pickedColor = guessHue === null ? '#ffffff' : hueToHex(guessHue)
  const uiColor = isWaitingScreen
    ? waitingColor
    : (activePhase === 'results' || activePhase === 'results_artist') &&
        roundResult
      ? roundResult.targetHex
      : activePhase === 'drawing' && drawingTarget
        ? drawingTarget.hex
        : pickedColor

  const handleCategoryPick = (choice: string) => {
    relayClient.send('host', {
      t: 'category_pick',
      category: choice,
    })
    setDrawingTarget((prev) => (prev ? { ...prev, category: choice } : prev))
  }

  const handleLeave = () => {
    relayClient.disconnect()
    navigate('/join')
  }

  const handlePlayNewGame = () => {
    // leave (not just disconnect) so the relay frees this name right away
    relayClient.leave()
    useLoginStore.getState().setRoom('')
    navigate('/join')
  }

  const renderWaitingTitle = () => {
    switch (status) {
      case 'connecting':
        return <>Joining room<BouncingDots /></>
      case 'reconnecting':
        return <>Reconnecting<BouncingDots /></>
      case 'host_lost':
        return <>Host disconnected<BouncingDots /></>
      case 'disconnected':
        return 'Disconnected'
      default:
        return <>Waiting for players<BouncingDots /></>
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
                    <TutorialTabs key="avatar" label="Your Avatar">
                      <LobbyPfpCanvas inkColor={waitingColor} />
                    </TutorialTabs>
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
        if (!drawingTarget) {
          return (
            <SquareHueSlider value={waitingHue} onChange={setWaitingHue}>
              <Card className="w-full select-none bg-black/80 ring-0 border-0 font-jersey">
                <div className="flex flex-col gap-2 p-2">
                  <CardHeader>
                    <CardTitle className="font-jersey sm:text-3xl text-2xl select-none text-center text-(--hue) [text-shadow:0_0_8px_#000,0_0_16px_#000]">
                      Artists Are Drawing
                    </CardTitle>
                    <CardDescription className="font-jersey select-none text-center sm:text-base text-sm text-(--hue)/80 [text-shadow:0_0_6px_#000,0_0_12px_#000]">
                      You&apos;re guessing this game. Hang tight, guessing starts once the artists are done<BouncingDots />
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
                    <HowToPlay />
                  </CardContent>
                </div>
              </Card>
            </SquareHueSlider>
          )
        }

        if (
          drawingTarget.categories !== null &&
          drawingTarget.category === null
        ) {
          return (
            <CategoryPickerCard
              targetHex={drawingTarget.hex}
              categories={drawingTarget.categories}
              deadlineMs={drawingDeadlineMs}
              onPick={handleCategoryPick}
            />
          )
        }

        return (
          <DrawingCanvasCard
            targetHex={drawingTarget.hex}
            category={drawingTarget.category}
            durationSec={drawingDurationSec}
            deadlineMs={drawingDeadlineMs}
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
                    Waiting for other players to finish drawing<BouncingDots />
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
                  {/* key: each screen starts on its own tab instead of inheriting the last screen's */}
                  <TutorialTabs key="drawing" label="Your Drawing">
                    <DrawingViewport
                      drawing={incomingDrawing}
                      color={drawingTarget?.hex ?? '#ffffff'}
                    />
                  </TutorialTabs>
                </CardContent>
              </div>
            </Card>
          </SquareHueSlider>
        )

      case 'results':
      case 'results_artist':
        return roundResult ? (
          <RoundResultsCard
            username={username}
            result={{
              ...roundResult,
              isArtist:
                activePhase === 'results_artist'
                  ? true
                  : activePhase === 'results' && DEV_MOCK_MODE
                    ? false
                    : roundResult.isArtist,
            }}
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
                    Waiting for host to start another round<BouncingDots />
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
                  <HowToPlay />

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
      className="fixed inset-0 h-dvh overflow-hidden overscroll-none touch-none bg-black flex items-center justify-center px-8 py-4 sm:px-6 sm:py-8"
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
          <TrueHueTitle
            overlapAnimate={isWaitingScreen}
            hueAngle={waitingHue}
            compact={
              (activePhase === 'drawing' && !isDrawingWaiting) ||
              activePhase === 'playing' ||
              activePhase === 'playing_done'
            }
          />
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
