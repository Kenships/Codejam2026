import { useEffect, useRef, useState, type PointerEvent } from 'react'

// full-saturation, full-brightness color for a hue: hueToHex(120) === '#00ff00'
// white 0–1 mixes it toward white: hueToHex(0, 1) === '#ffffff'
export const hueToHex = (hue: number, white = 0) => {
  const channel = (n: number) => {
    const k = (n + hue / 60) % 6
    const v = 1 - Math.max(0, Math.min(k, 4 - k, 1))
    return Math.round((v + (1 - v) * white) * 255).toString(16).padStart(2, '0')
  }
  return `#${channel(5)}${channel(3)}${channel(1)}`
}

const STOPS = ['white', 'red', 'yellow', 'lime', 'cyan', 'blue', 'magenta', 'red']
const BAR = `linear-gradient(to right, ${STOPS.join(', ')})`
const WHITE_END = 1 / (STOPS.length - 1) // white fades into red over the first segment

// color shown at a position (0–1) along the bar, matching the gradient exactly
export const colorAt = (pos: number) =>
  pos < WHITE_END
    ? hueToHex(0, 1 - pos / WHITE_END)
    : hueToHex(((pos - WHITE_END) / (1 - WHITE_END)) * 360)

const clamp01 = (n: number) => Math.min(Math.max(n, 0), 1)

type Props = {
  value: number | null // position 0–1 along the bar, null = nothing picked yet
  onChange: (pos: number | null) => void
}

// solid block of the current color; click once to open & snap to the mouse,
// slider + hue follow the mouse while open; click bar (or drag & release) to confirm,
// click outside or press Escape to cancel
export function HueBar({ value, onChange }: Props) {
  const [open, setOpen] = useState(false)
  const barRef = useRef<HTMLDivElement>(null)
  const picking = useRef(false)
  const openedOnPress = useRef(false)
  const dragged = useRef(false)
  const startX = useRef(0)
  const prevValue = useRef<number | null>(value)

  const posAtClientX = (clientX: number) => {
    const r = barRef.current?.getBoundingClientRect()
    if (!r) return 0
    return clamp01((clientX - r.left) / r.width)
  }

  useEffect(() => {
    if (!open) return

    const onWindowPointerMove = (e: globalThis.PointerEvent) => {
      // while open, snap slider + hue to the mouse horizontally without needing to hold
      if (e.pointerType === 'mouse' && !picking.current) {
        onChange(posAtClientX(e.clientX))
      }
    }

    const onOutsidePointerDown = (e: globalThis.PointerEvent) => {
      if (barRef.current?.contains(e.target as Node)) return
      picking.current = false
      onChange(prevValue.current)
      setOpen(false)
    }

    const onWindowKeyDown = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return
      picking.current = false
      onChange(prevValue.current)
      setOpen(false)
    }

    window.addEventListener('pointermove', onWindowPointerMove)
    window.addEventListener('pointerdown', onOutsidePointerDown, true)
    window.addEventListener('keydown', onWindowKeyDown)
    return () => {
      window.removeEventListener('pointermove', onWindowPointerMove)
      window.removeEventListener('pointerdown', onOutsidePointerDown, true)
      window.removeEventListener('keydown', onWindowKeyDown)
    }
  }, [open, onChange])

  return (
    <div
      ref={barRef}
      role="slider"
      aria-label="Color"
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={value === null ? undefined : Math.round(value * 100)}
      tabIndex={0}
      draggable={false}
      className="relative h-12 sm:h-3 w-full cursor-pointer select-none touch-none [-webkit-touch-callout:none] border-2 border-(--hue)"
      style={{ background: open ? BAR : value === null ? '#ffffff' : colorAt(value) }}
      onDragStart={(e) => e.preventDefault()}
      onPointerDown={(e: PointerEvent<HTMLDivElement>) => {
        e.preventDefault()
        if (!open) {
          prevValue.current = value
          openedOnPress.current = true
          setOpen(true)
        } else {
          openedOnPress.current = false
        }
        dragged.current = false
        startX.current = e.clientX
        picking.current = true
        e.currentTarget.setPointerCapture(e.pointerId)
        onChange(posAtClientX(e.clientX))
      }}
      onPointerMove={(e: PointerEvent<HTMLDivElement>) => {
        if (!picking.current) return
        if (Math.abs(e.clientX - startX.current) > 4) dragged.current = true
        onChange(posAtClientX(e.clientX))
      }}
      onPointerUp={() => {
        if (!picking.current) return
        picking.current = false
        if (openedOnPress.current && !dragged.current) {
          openedOnPress.current = false
          return
        }
        setOpen(false)
      }}
      onPointerCancel={() => {
        picking.current = false
      }}
      onKeyDown={(e) => {
        const step = { ArrowRight: 0.02, ArrowUp: 0.02, ArrowLeft: -0.02, ArrowDown: -0.02 }[e.key]
        if (!step) return
        e.preventDefault()
        onChange(clamp01((value ?? 0) + step))
      }}
    >
      {open && value !== null && (
        <div
          className="pointer-events-none border-2 rounded-sm absolute inset-y-0 w-4 -translate-x-1/2 bg-white shadow-[0_0_4px_black]"
          style={{ left: `${value * 100}%` }}
        />
      )}
    </div>
  )
}
