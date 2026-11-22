import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type PointerEvent as ReactPointerEvent,
} from 'react'
import { Check, Eraser, Trash2, Undo2 } from 'lucide-react'

import { BouncingDots } from '@/components/ui/bouncing-dots'
import { Button } from '@/components/ui/button'
import {
  compressWhiteDrawingImage,
  renderStrokesToContext,
  type DrawingStroke,
  type NormalizedPoint,
} from '@/lib/drawingCodec'
import { relayClient } from '@/lib/relayClient'
import { useLoginStore } from '@/store/useLoginStore'

interface LobbyPfpCanvasProps {
  /** Active UI hue colour used to render the PFP strokes for the player */
  inkColor: string
}

const BRUSH_SIZES = [
  { label: 'S', value: 3 },
  { label: 'M', value: 6 },
  { label: 'L', value: 12 },
] as const

const PFP_MAX_PX = 256
const MIN_POINT_DIST_SQ = 0.003 * 0.003

function clamp01(n: number): number {
  return Math.max(0, Math.min(1, n))
}

function quantizeCoord(n: number): number {
  return Math.round(clamp01(n) * 1000) / 1000
}

export function LobbyPfpCanvas({ inkColor }: LobbyPfpCanvasProps) {
  const setPfp = useLoginStore((s) => s.setPfp)

  const initialStrokes = Array.isArray(useLoginStore.getState().pfpStrokes)
    ? useLoginStore.getState().pfpStrokes
    : []

  const canvasRef = useRef<HTMLCanvasElement | null>(null)
  const strokesRef = useRef<DrawingStroke[]>(initialStrokes)
  const activeStrokeRef = useRef<DrawingStroke | null>(null)
  const inkColorRef = useRef(inkColor)

  const [strokesCount, setStrokesCount] = useState(() => initialStrokes.length)
  const [brushSize, setBrushSize] = useState<number>(BRUSH_SIZES[1].value)
  const [isEraser, setIsEraser] = useState(false)
  const [saved, setSaved] = useState(
    () =>
      initialStrokes.length > 0 &&
      Boolean(useLoginStore.getState().pfpCompressed)
  )
  const [saving, setSaving] = useState(false)

  const redrawCanvas = useCallback(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    const ctx = canvas.getContext('2d')
    if (!ctx) return

    const width = canvas.clientWidth
    const height = canvas.clientHeight
    if (width === 0 || height === 0) return

    const dpr = window.devicePixelRatio || 1
    const scale = Math.min(dpr, PFP_MAX_PX / Math.max(width, height))
    const pixelWidth = Math.min(
      PFP_MAX_PX,
      Math.max(1, Math.round(width * scale))
    )
    const pixelHeight = Math.min(
      PFP_MAX_PX,
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

    renderStrokesToContext(ctx, width, height, allStrokes, inkColorRef.current)
  }, [])

  useEffect(() => {
    inkColorRef.current = inkColor
    redrawCanvas()
  }, [inkColor, redrawCanvas])

  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    redrawCanvas()

    const observer = new ResizeObserver(() => redrawCanvas())
    observer.observe(canvas)
    return () => observer.disconnect()
  }, [redrawCanvas])

  const getNormalizedPoint = (
    e: ReactPointerEvent<HTMLCanvasElement>
  ): NormalizedPoint => {
    const rect = e.currentTarget.getBoundingClientRect()
    const nx = quantizeCoord((e.clientX - rect.left) / rect.width)
    const ny = quantizeCoord((e.clientY - rect.top) / rect.height)
    return [nx, ny]
  }

  const handlePointerDown = (e: ReactPointerEvent<HTMLCanvasElement>) => {
    e.preventDefault()
    e.currentTarget.setPointerCapture(e.pointerId)
    if (saved) setSaved(false)

    const pt = getNormalizedPoint(e)
    activeStrokeRef.current = {
      pts: [pt],
      size: isEraser ? brushSize * 2 : brushSize,
      eraser: isEraser,
    }
    redrawCanvas()
  }

  const handlePointerMove = (e: ReactPointerEvent<HTMLCanvasElement>) => {
    if (!activeStrokeRef.current) return
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
    redrawCanvas()
  }

  const persistStrokes = useCallback(
    async (strokesSnapshot: DrawingStroke[], sendToRelay: boolean) => {
      if (strokesSnapshot.length === 0) {
        setPfp(null)
        setSaved(false)
        return
      }
      setSaving(true)
      try {
        const canvas = canvasRef.current
        const exported = await compressWhiteDrawingImage({
          cssWidth: canvas?.clientWidth || PFP_MAX_PX,
          cssHeight: canvas?.clientHeight || PFP_MAX_PX,
          strokes: strokesSnapshot,
        })

        setPfp({
          dataUrl: exported.dataUrl,
          strokes: strokesSnapshot,
          compressed: {
            base64: exported.base64,
            mime: exported.mime,
            width: exported.width,
            height: exported.height,
          },
        })
        if (sendToRelay) {
          relayClient.send('host', {
            t: 'pfp_submit',
            image: exported.base64,
            mime: exported.mime,
            compression: exported.compression,
            w: exported.width,
            h: exported.height,
          })
          setSaved(true)
        }
      } finally {
        setSaving(false)
      }
    },
    [setPfp]
  )

  const finishStroke = () => {
    if (!activeStrokeRef.current) return
    const nextStrokes = [...strokesRef.current, activeStrokeRef.current]
    strokesRef.current = nextStrokes
    activeStrokeRef.current = null
    setStrokesCount(nextStrokes.length)
    redrawCanvas()
    // Auto-save locally so joining from LoginCard includes the avatar even without pressing Save PFP
    void persistStrokes(nextStrokes, false)
  }

  const handleUndo = () => {
    if (strokesRef.current.length === 0) return
    const nextStrokes = strokesRef.current.slice(0, -1)
    strokesRef.current = nextStrokes
    setStrokesCount(nextStrokes.length)
    setSaved(false)
    redrawCanvas()
    void persistStrokes(nextStrokes, false)
  }

  const handleClear = () => {
    if (strokesRef.current.length === 0) return
    strokesRef.current = []
    activeStrokeRef.current = null
    setStrokesCount(0)
    setSaved(false)
    setPfp(null)
    redrawCanvas()
  }

  const handleSavePfp = async () => {
    if (saving || strokesRef.current.length === 0) return
    await persistStrokes([...strokesRef.current], true)
  }

  return (
    <div className="flex w-full flex-col items-center gap-1.5 pt-0.5">
      <span className="text-xs uppercase tracking-widest text-(--hue)/75">
        Draw Your Avatar
      </span>

      <div className="relative aspect-square h-28 w-28 max-h-[16dvh] max-w-[16dvh] min-h-20 min-w-20 overflow-hidden border-2 border-(--hue) bg-black">
        <canvas
          ref={canvasRef}
          onPointerDown={handlePointerDown}
          onPointerMove={handlePointerMove}
          onPointerUp={finishStroke}
          onPointerCancel={finishStroke}
          className="h-full w-full cursor-crosshair touch-none"
        />
      </div>

      {/* Compact brush & canvas tools */}
      <div className="flex flex-wrap items-center justify-center gap-1.5">
        {BRUSH_SIZES.map((brush) => (
          <button
            key={brush.label}
            type="button"
            onClick={() => {
              setIsEraser(false)
              setBrushSize(brush.value)
            }}
            className={`h-7 w-7 border-2 text-xs cursor-pointer ${
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
          className={`flex h-7 w-7 items-center justify-center border-2 cursor-pointer ${
            isEraser
              ? 'border-(--hue) bg-(--hue) text-black'
              : 'border-(--hue)/60 bg-black/60 text-(--hue) hover:border-(--hue)'
          }`}
        >
          <Eraser className="h-3.5 w-3.5" />
        </button>

        <button
          type="button"
          disabled={strokesCount === 0}
          aria-label="Undo"
          title="Undo"
          onClick={handleUndo}
          className="flex h-7 w-7 items-center justify-center border-2 border-(--hue)/60 bg-black/60 text-(--hue) cursor-pointer hover:border-(--hue) disabled:opacity-40"
        >
          <Undo2 className="h-3.5 w-3.5" />
        </button>

        <button
          type="button"
          disabled={strokesCount === 0}
          aria-label="Clear avatar"
          title="Clear"
          onClick={handleClear}
          className="flex h-7 w-7 items-center justify-center border-2 border-(--hue)/60 bg-black/60 text-(--hue) cursor-pointer hover:border-(--hue) disabled:opacity-40"
        >
          <Trash2 className="h-3.5 w-3.5" />
        </button>

        <Button
          type="button"
          disabled={strokesCount === 0 || saving}
          onClick={handleSavePfp}
          className="h-7 transition-none select-none bg-alpha text-(--hue) border-2 border-(--hue) font-jersey px-2.5 py-0 text-xs hover:bg-(--hue) hover:text-black disabled:opacity-40"
        >
          {saved ? (
            <span className="inline-flex items-center gap-1">
              <Check className="h-3 w-3" />
              Saved
            </span>
          ) : saving ? (
            <span className="inline-flex items-center">
              Saving
              <BouncingDots />
            </span>
          ) : (
            'Save PFP'
          )}
        </Button>
      </div>
    </div>
  )
}
