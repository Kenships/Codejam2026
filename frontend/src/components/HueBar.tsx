import { useRef, useState, type PointerEvent } from 'react'

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
  onChange: (pos: number) => void
}

// solid block of the current color; click it to reveal the gradient, then click/drag to pick
export function HueBar({ value, onChange }: Props) {
  const [open, setOpen] = useState(false)
  const picking = useRef(false)

  const posAt = (e: PointerEvent<HTMLDivElement>) => {
    const r = e.currentTarget.getBoundingClientRect()
    return clamp01((e.clientX - r.left) / r.width)
  }

  return (
    <div
      role="slider"
      aria-label="Color"
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={value === null ? undefined : Math.round(value * 100)}
      tabIndex={0}
      className="relative h-12 sm:h-3 w-full cursor-pointer touch-none border-2 border-(--hue)"
      style={{ background: open ? BAR : value === null ? '#ffffff' : colorAt(value) }}
      onPointerDown={(e) => {
        if (!open) return setOpen(true) 
        picking.current = true
        e.currentTarget.setPointerCapture(e.pointerId)
        onChange(posAt(e))
      }}
      onPointerMove={(e) => {
        if (picking.current) onChange(posAt(e))
      }}
      onPointerUp={() => {
        if (!picking.current) return
        picking.current = false
        setOpen(false)
      }}
      onPointerLeave={(e) => {
        // touch fires pointerleave right after every tap, which would close the bar before you can pick
        if (e.pointerType === 'mouse' && !picking.current) setOpen(false)
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
