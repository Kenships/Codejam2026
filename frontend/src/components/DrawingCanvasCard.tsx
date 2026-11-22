import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type FormEvent,
  type PointerEvent as ReactPointerEvent,
} from 'react'
import { Check, Eraser, Maximize2, Minimize2, Trash2, Undo2 } from 'lucide-react'

import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import {
  MAX_CANVAS_PX,
  compressWhiteDrawingImage,
  renderStrokesToContext,
  type CompressedDrawingImage,
  type DrawingMime,
  type DrawingStroke,
  type NormalizedPoint,
} from '@/lib/drawingCodec'
import { relayClient } from '@/lib/relayClient'

export type { DrawingStroke, NormalizedPoint }

export interface DrawingSubmission {
  prompt: string
  strokes: DrawingStroke[]
  /** Uncompressed white-on-black data URL for local preview */
  imageDataUrl: string
  /** GZIP-compressed 1-bit bitmap encoded as base64 (no data: prefix) */
  imageBase64: string
  mime: DrawingMime
  compression: CompressedDrawingImage['compression']
  width: number
  height: number
}

interface DrawingCanvasCardProps {
  /** Exact target colour hex received from the API (e.g. "#FF5733") */
  targetHex: string
  /** Chosen category prompt (PROTOCOL.md §7); replaces free-text input when set */
  category?: string | null
  /** Round drawing duration in seconds (default 60s) */
  durationSec?: number
  /** Absolute local deadline timestamp (ms) derived from state.remainingMs - ageMs */
  deadlineMs?: number | null
  /** Force submitted/waiting state for dev preview */
  forceSubmitted?: boolean
  onSubmit?: (submission: DrawingSubmission) => void
}

const BRUSH_SIZES = [
  { label: 'S', value: 3 },
  { label: 'M', value: 7 },
  { label: 'L', value: 14 },
] as const

const PROMPT_MAX_LENGTH = 80
/** PROTOCOL.md §7: stroke_batch sent every ~100 ms (~10 msg/s, well under 40 msg/s rate limit) */
const STROKE_BATCH_INTERVAL_MS = 100
const MAX_PTS_PER_BATCH = 64
/** Minimum squared normalized movement (~1 px on a 512x512 canvas) to avoid duplicate points */
const MIN_POINT_DIST_SQ = 0.002 * 0.002

function clamp01(n: number): number {
  return Math.max(0, Math.min(1, n))
}

function quantizeCoord(n: number): number {
  return Math.round(clamp01(n) * 1000) / 1000
}

export function DrawingCanvasCard({
  targetHex,
  category = null,
  durationSec = 60,
  deadlineMs = null,
  forceSubmitted = false,
  onSubmit,
}: DrawingCanvasCardProps) {
  const canvasRef = useRef<HTMLCanvasElement | null>(null)
  const strokesRef = useRef<DrawingStroke[]>([])
  const activeStrokeRef = useRef<DrawingStroke | null>(null)
  const pendingBatchRef = useRef<{
    pts: NormalizedPoint[]
    size: number
    eraser: boolean
  } | null>(null)
  const submittedRef = useRef(false)

  const [fallbackDeadlineMs] = useState(() => Date.now() + durationSec * 1000)
  const effectiveDeadlineMs = deadlineMs ?? fallbackDeadlineMs

  const [strokesCount, setStrokesCount] = useState(0)
  const [brushSize, setBrushSize] = useState<number>(BRUSH_SIZES[1].value)
  const [isEraser, setIsEraser] = useState(false)
  const [prompt, setPrompt] = useState('')
  const [secondsLeft, setSecondsLeft] = useState(() =>
    Math.max(0, Math.ceil((effectiveDeadlineMs - Date.now()) / 1000))
  )
  const [localSubmitted, setLocalSubmitted] = useState(false)
  const [isFullscreen, setIsFullscreen] = useState(false)
  const submitted = localSubmitted || forceSubmitted

  // Exit fullscreen automatically on Escape key or when submission completes
  useEffect(() => {
    if (!isFullscreen) return
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setIsFullscreen(false)
      }
    }
    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [isFullscreen])

  const toggleFullscreen = () => {
    setIsFullscreen((prev) => {
      const next = !prev
      if (next) {
        document.documentElement.requestFullscreen?.().catch(() => {
          // Fallback to CSS fixed fullscreen (e.g. iOS Safari)
        })
      } else if (document.fullscreenElement) {
        document.exitFullscreen?.().catch(() => {})
      }
      return next
    })
  }

  const promptRef = useRef(prompt)
  useEffect(() => {
    promptRef.current = prompt
  }, [prompt])

  const categoryRef = useRef(category)
  useEffect(() => {
    categoryRef.current = category
  }, [category])

  const onSubmitRef = useRef(onSubmit)
  useEffect(() => {
    onSubmitRef.current = onSubmit
  }, [onSubmit])

  const redrawCanvas = useCallback(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    const ctx = canvas.getContext('2d')
    if (!ctx) return

    const width = canvas.clientWidth
    const height = canvas.clientHeight
    if (width === 0 || height === 0) return

    const dpr = window.devicePixelRatio || 1
    const scale = Math.min(dpr, MAX_CANVAS_PX / Math.max(width, height))
    const pixelWidth = Math.min(
      MAX_CANVAS_PX,
      Math.max(1, Math.round(width * scale))
    )
    const pixelHeight = Math.min(
      MAX_CANVAS_PX,
      Math.max(1, Math.round(height * scale))
    )

    if (canvas.width !== pixelWidth || canvas.height !== pixelHeight) {
      canvas.width = pixelWidth
      canvas.height = pixelHeight
    }

    ctx.setTransform(pixelWidth / width, 0, 0, pixelHeight / height, 0, 0)

    const allStrokes = activeStrokeRef.current
      ? [...strokesRef.current, activeStrokeRef.current]
      : strokesRef.current

    // Artist sees strokes in the target colour; backend export renders them in white
    renderStrokesToContext(ctx, width, height, allStrokes, targetHex)
  }, [targetHex])

  // Resize observer so the canvas stays sharp on mobile & desktop resizes
  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    redrawCanvas()

    const observer = new ResizeObserver(() => redrawCanvas())
    observer.observe(canvas)
    return () => observer.disconnect()
  }, [redrawCanvas])

  // Flush batched normalized stroke points every ~100ms per PROTOCOL.md §6 & §7
  const flushStrokeBatch = useCallback(() => {
    const batch = pendingBatchRef.current
    if (!batch || batch.pts.length === 0) return

    const { pts, size, eraser } = batch
    pendingBatchRef.current = null

    for (let i = 0; i < pts.length; i += MAX_PTS_PER_BATCH) {
      const chunk = pts.slice(i, i + MAX_PTS_PER_BATCH)
      const payload: Record<string, unknown> = {
        t: 'stroke_batch',
        pts: chunk,
        size,
      }
      if (eraser) {
        payload.eraser = true
      }
      // Strokes go to the host only: Unity draws them live; phones never display them
      relayClient.send('host', payload)
    }
  }, [])

  useEffect(() => {
    const intervalId = window.setInterval(
      flushStrokeBatch,
      STROKE_BATCH_INTERVAL_MS
    )
    return () => {
      window.clearInterval(intervalId)
      flushStrokeBatch()
    }
  }, [flushStrokeBatch])

  const finalizeSubmission = useCallback(async () => {
    if (submittedRef.current) return
    submittedRef.current = true
    activeStrokeRef.current = null
    flushStrokeBatch()
    setLocalSubmitted(true)
    setIsFullscreen(false)
    if (document.fullscreenElement) {
      document.exitFullscreen?.().catch(() => {})
    }

    const effectivePrompt = categoryRef.current ?? promptRef.current.trim()
    const canvas = canvasRef.current
    const exported = await compressWhiteDrawingImage({
      cssWidth: canvas?.clientWidth || MAX_CANVAS_PX,
      cssHeight: canvas?.clientHeight || MAX_CANVAS_PX,
      strokes: strokesRef.current,
    })

    // Send GZIP-compressed white-on-black image to "host" (always permitted under PROTOCOL.md §3)
    relayClient.send('host', {
      t: 'drawing_submit',
      prompt: effectivePrompt,
      image: exported.base64,
      mime: exported.mime,
      compression: exported.compression,
      w: exported.width,
      h: exported.height,
    })

    onSubmitRef.current?.({
      prompt: effectivePrompt,
      strokes: [...strokesRef.current],
      imageDataUrl: exported.dataUrl,
      imageBase64: exported.base64,
      mime: exported.mime,
      compression: exported.compression,
      width: exported.width,
      height: exported.height,
    })
  }, [flushStrokeBatch])

  // Countdown timer (durationSec, set by the host); auto-submits when time expires
  useEffect(() => {
    if (submitted) return

    const update = () => {
      const remaining = Math.max(
        0,
        Math.ceil((effectiveDeadlineMs - Date.now()) / 1000)
      )
      setSecondsLeft(remaining)
      if (remaining === 0) {
        void finalizeSubmission()
        return true
      }
      return false
    }

    if (update()) return

    const intervalId = window.setInterval(() => {
      if (update()) {
        window.clearInterval(intervalId)
      }
    }, 250)

    return () => window.clearInterval(intervalId)
  }, [effectiveDeadlineMs, submitted, finalizeSubmission])

  const getNormalizedPoint = (
    e: ReactPointerEvent<HTMLCanvasElement>
  ): NormalizedPoint => {
    const rect = e.currentTarget.getBoundingClientRect()
    const nx = quantizeCoord((e.clientX - rect.left) / rect.width)
    const ny = quantizeCoord((e.clientY - rect.top) / rect.height)
    return [nx, ny]
  }

  const queuePointForBatch = (
    pt: NormalizedPoint,
    size: number,
    eraser: boolean
  ) => {
    if (!pendingBatchRef.current) {
      pendingBatchRef.current = { pts: [pt], size, eraser }
      return
    }
    pendingBatchRef.current.pts.push(pt)
  }

  const handlePointerDown = (e: ReactPointerEvent<HTMLCanvasElement>) => {
    if (submitted) return
    e.preventDefault()
    e.currentTarget.setPointerCapture(e.pointerId)

    // Flush any remaining points from a previous stroke before starting a new one
    flushStrokeBatch()

    const pt = getNormalizedPoint(e)
    const effectiveSize = isEraser ? brushSize * 2 : brushSize
    activeStrokeRef.current = {
      pts: [pt],
      size: effectiveSize,
      eraser: isEraser,
    }
    queuePointForBatch(pt, effectiveSize, isEraser)
    redrawCanvas()
  }

  const handlePointerMove = (e: ReactPointerEvent<HTMLCanvasElement>) => {
    if (submitted || !activeStrokeRef.current) return
    e.preventDefault()
    const pt = getNormalizedPoint(e)
    const currentPts = activeStrokeRef.current.pts
    const lastPt = currentPts[currentPts.length - 1]
    if (lastPt) {
      const dx = pt[0] - lastPt[0]
      const dy = pt[1] - lastPt[1]
      if (dx * dx + dy * dy < MIN_POINT_DIST_SQ) {
        return
      }
    }

    currentPts.push(pt)
    queuePointForBatch(
      pt,
      activeStrokeRef.current.size,
      Boolean(activeStrokeRef.current.eraser)
    )
    redrawCanvas()
  }

  const finishStroke = () => {
    if (!activeStrokeRef.current) return
    strokesRef.current = [...strokesRef.current, activeStrokeRef.current]
    activeStrokeRef.current = null
    setStrokesCount(strokesRef.current.length)
    flushStrokeBatch()
    redrawCanvas()
  }

  const handleUndo = () => {
    if (submitted || strokesRef.current.length === 0) return
    strokesRef.current = strokesRef.current.slice(0, -1)
    setStrokesCount(strokesRef.current.length)
    relayClient.send('host', { t: 'undo' })
    redrawCanvas()
  }

  const handleClear = () => {
    if (submitted || strokesRef.current.length === 0) return
    strokesRef.current = []
    activeStrokeRef.current = null
    pendingBatchRef.current = null
    setStrokesCount(0)
    relayClient.send('host', { t: 'clear' })
    redrawCanvas()
  }

  const handleFormSubmit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    void finalizeSubmission()
  }

  const displayPrompt = (category ?? prompt).trim()
  const progressPct =
    durationSec > 0
      ? Math.max(0, Math.min(100, (secondsLeft / durationSec) * 100))
      : 0
  const isUrgent = secondsLeft <= 10 && !submitted

  return (
    <Card
      className={
        isFullscreen
          ? 'fixed inset-0 z-40 flex h-dvh w-screen flex-col justify-between select-none rounded-none border-0 bg-black/95 ring-0 font-jersey text-(--hue)'
          : 'w-full select-none bg-black/80 ring-0 border-2 border-(--hue) font-jersey text-(--hue)'
      }
    >
      <CardHeader className="pb-2 pt-3 px-3 sm:px-4 shrink-0">
        <div className="flex items-center justify-between gap-2">
          <div className="flex items-center gap-2 min-w-0">
            <span
              className="h-6 w-6 shrink-0 rounded border-2 border-white/80"
              style={{ backgroundColor: targetHex }}
              aria-label={`Target colour ${targetHex}`}
            />
            <div className="flex flex-col leading-tight min-w-0">
              <CardTitle className="font-jersey text-lg sm:text-xl text-(--hue) [text-shadow:0_0_8px_#000]">
                Draw Your Colour
              </CardTitle>
              <span className="text-xs tracking-wider uppercase text-(--hue)/75">
                Target: {targetHex.toUpperCase()}
              </span>
            </div>
          </div>

          <div className="flex items-center gap-1.5">
            {!submitted && (
              <button
                type="button"
                onClick={toggleFullscreen}
                aria-label={isFullscreen ? 'Exit fullscreen' : 'Fullscreen drawing'}
                title={isFullscreen ? 'Exit fullscreen' : 'Fullscreen'}
                className="flex h-8 w-8 items-center justify-center rounded border-2 border-(--hue)/70 bg-black/60 text-(--hue) cursor-pointer hover:border-(--hue) hover:bg-(--hue) hover:text-black"
              >
                {isFullscreen ? (
                  <Minimize2 className="h-4 w-4" />
                ) : (
                  <Maximize2 className="h-4 w-4" />
                )}
              </button>
            )}

            <span
              className={`rounded border-2 px-2.5 py-0.5 text-lg sm:text-xl tabular-nums ${
                isUrgent
                  ? 'border-red-400 text-red-400 animate-pulse'
                  : 'border-(--hue) text-(--hue)'
              }`}
            >
              {submitted ? 'Done' : `${secondsLeft}s`}
            </span>
          </div>
        </div>

        {/* Countdown bar */}
        <div className="mt-2 h-1.5 w-full overflow-hidden rounded-full bg-white/15">
          <div
            className={`h-full transition-[width] duration-200 ${
              isUrgent ? 'bg-red-400' : 'bg-(--hue)'
            }`}
            style={{ width: `${progressPct}%` }}
          />
        </div>
      </CardHeader>

      <CardContent
        className={`flex flex-col gap-2.5 px-3 sm:px-4 pb-3 ${
          isFullscreen ? 'min-h-0 flex-1 justify-center' : ''
        }`}
      >
        {/* Drawing canvas (square, internal bitmap capped at 512x512) */}
        <div
          className={`relative mx-auto aspect-square w-full overflow-hidden rounded border-2 border-(--hue) bg-black ${
            isFullscreen
              ? 'max-w-[min(92vw,calc(100dvh-13rem))]'
              : 'max-w-[min(512px,42dvh)]'
          }`}
        >
          <canvas
            ref={canvasRef}
            onPointerDown={handlePointerDown}
            onPointerMove={handlePointerMove}
            onPointerUp={finishStroke}
            onPointerCancel={finishStroke}
            className={`h-full w-full touch-none ${
              submitted ? 'cursor-default opacity-85' : 'cursor-crosshair'
            }`}
          />
        </div>

        {submitted ? (
          <div className="flex w-full flex-col items-center gap-1 rounded border-2 border-(--hue) bg-black/60 px-3 py-2.5 text-center">
            {displayPrompt && (
              <p className="text-sm sm:text-base text-(--hue)/85">
                {category ? `Prompt: ${displayPrompt}` : `Clue: "${displayPrompt}"`}
              </p>
            )}
            <div className="flex items-center gap-1.5">
              <Check className="h-4 w-4 text-(--hue)" />
              <span className="text-base sm:text-lg text-(--hue)">
                Waiting for other players to finish...
              </span>
            </div>
          </div>
        ) : (
          <>
            {/* Tools row */}
            <div className="flex items-center justify-between gap-2">
              <div className="flex items-center gap-1">
                {BRUSH_SIZES.map((brush) => (
                  <button
                    key={brush.label}
                    type="button"
                    onClick={() => {
                      setIsEraser(false)
                      setBrushSize(brush.value)
                    }}
                    className={`h-8 w-8 rounded border-2 text-sm cursor-pointer ${
                      !isEraser && brushSize === brush.value
                        ? 'border-(--hue) bg-(--hue) text-black font-semibold'
                        : 'border-(--hue)/60 bg-black/60 text-(--hue) hover:border-(--hue)'
                    }`}
                  >
                    {brush.label}
                  </button>
                ))}

                <button
                  type="button"
                  aria-label="Eraser"
                  title="Eraser"
                  onClick={() => setIsEraser((prev) => !prev)}
                  className={`flex h-8 w-8 items-center justify-center rounded border-2 cursor-pointer ${
                    isEraser
                      ? 'border-(--hue) bg-(--hue) text-black'
                      : 'border-(--hue)/60 bg-black/60 text-(--hue) hover:border-(--hue)'
                  }`}
                >
                  <Eraser className="h-4 w-4" />
                </button>
              </div>

              <div className="flex items-center gap-1">
                <button
                  type="button"
                  disabled={strokesCount === 0}
                  aria-label="Undo"
                  title="Undo"
                  onClick={handleUndo}
                  className="flex h-8 items-center gap-1 rounded border-2 border-(--hue)/60 bg-black/60 px-2 text-sm text-(--hue) cursor-pointer hover:border-(--hue) disabled:opacity-40"
                >
                  <Undo2 className="h-3.5 w-3.5" />
                  <span>Undo</span>
                </button>

                <button
                  type="button"
                  disabled={strokesCount === 0}
                  aria-label="Clear canvas"
                  title="Clear"
                  onClick={handleClear}
                  className="flex h-8 items-center gap-1 rounded border-2 border-(--hue)/60 bg-black/60 px-2 text-sm text-(--hue) cursor-pointer hover:border-(--hue) disabled:opacity-40"
                >
                  <Trash2 className="h-3.5 w-3.5" />
                  <span>Clear</span>
                </button>
              </div>
            </div>

            {/* Chosen category prompt (or legacy free-text clue input) + Submit */}
            <form onSubmit={handleFormSubmit} className="flex gap-2">
              {category !== null ? (
                <div className="flex min-w-0 flex-1 items-center gap-1.5 border-2 border-(--hue)/70 bg-black/50 px-2.5 py-1.5 text-base text-(--hue)">
                  <span className="shrink-0 text-(--hue)/70">Prompt:</span>
                  <span className="truncate font-semibold">{category}</span>
                </div>
              ) : (
                <input
                  type="text"
                  placeholder="Write a clue for your colour..."
                  maxLength={PROMPT_MAX_LENGTH}
                  value={prompt}
                  onChange={(e) => setPrompt(e.target.value)}
                  className="min-w-0 flex-1 border-2 border-(--hue) font-jersey bg-black/50 px-2.5 py-1.5 text-base text-(--hue) placeholder:text-(--hue)/60"
                />
              )}
              <Button
                type="submit"
                className="shrink-0 transition-none select-none bg-alpha text-(--hue) border-2 border-(--hue) font-jersey px-3 py-1.5 hover:bg-(--hue) hover:text-black"
              >
                Submit
              </Button>
            </form>
          </>
        )}
      </CardContent>
    </Card>
  )
}
