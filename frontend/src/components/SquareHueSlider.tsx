import { useEffect, useRef, useState, type PointerEvent, type ReactNode } from 'react'

type Props = {
  value: number | null // hue 0–360, null = nothing picked yet (no thumb)
  onChange: (hue: number) => void
  onHover?: (hue: number | null) => void // hue under the pointer, null when it leaves
  thickness?: number
  children: ReactNode // the slider wraps around this as its border
}

// full-saturation, full-brightness color for a hue: hueToHex(120) === '#00ff00'
// white 0–1 mixes it toward white: hueToHex(120, 1) === '#ffffff'
export const hueToHex = (hue: number, white = 0) => {
  const channel = (n: number) => {
    const k = (n + hue / 60) % 6
    const v = 1 - Math.max(0, Math.min(k, 4 - k, 1))
    return Math.round((v + (1 - v) * white) * 255).toString(16).padStart(2, '0')
  }
  return `#${channel(5)}${channel(3)}${channel(1)}`
}

const RAINBOW = 'conic-gradient(red, yellow, lime, cyan, blue, magenta, red)'
// hollows out the center so only a `thickness`-wide band of the gradient shows
const RING_MASK = 'linear-gradient(#000 0 0) content-box exclude, linear-gradient(#000 0 0)'
// Extra invisible touch area extending outward around the ring for easier mobile grabbing
const TOUCH_SLOP = 14

export function SquareHueSlider({ value, onChange, onHover, thickness = 16, children }: Props) {
  const ref = useRef<HTMLDivElement>(null)
  const draggingRef = useRef(false)
  const [box, setBox] = useState({ w: 0, h: 0 })

  useEffect(() => {
    const el = ref.current!
    const ro = new ResizeObserver(() => setBox({ w: el.offsetWidth, h: el.offsetHeight }))
    ro.observe(el)
    return () => ro.disconnect()
  }, [])

  // hue = angle from center, clockwise from top (same origin as conic-gradient)
  const hueAt = (e: PointerEvent) => {
    const r = ref.current!.getBoundingClientRect()
    const x = e.clientX - r.left - r.width / 2
    const y = e.clientY - r.top - r.height / 2
    return ((Math.atan2(x, -y) * 180) / Math.PI + 360) % 360
  }

  const ring = { padding: thickness, mask: RING_MASK, background: RAINBOW }
  const thumb = Math.max(24, Math.round(thickness * 1.75))

  let thumbPos = null
  if (value !== null && box.w) {
    // push the hue's direction out until it hits the rectangle, along the band's midline
    const rad = (value * Math.PI) / 180
    const dx = Math.sin(rad)
    const dy = -Math.cos(rad)
    const reach = Math.min(
      (box.w - thickness) / 2 / Math.abs(dx),
      (box.h - thickness) / 2 / Math.abs(dy),
    )
    thumbPos = { left: box.w / 2 + dx * reach - thumb / 2, top: box.h / 2 + dy * reach - thumb / 2 }
  }

  return (
    <div
      ref={ref}
      // No text selection / long-press menu / tap flash anywhere in the slider: on phones a held
      // finger on the ring otherwise selects this wrapper and highlights the whole card.
      className="relative select-none [-webkit-touch-callout:none] [-webkit-tap-highlight-color:transparent]"
      style={{ padding: thickness }}
      onContextMenu={(e) => e.preventDefault()}
    >
      {/* soft neon glow behind the band */}
      <div className="pointer-events-none absolute inset-0 opacity-70 blur-md" style={ring} />
      {/* visible rainbow ring */}
      <div className="pointer-events-none absolute inset-0" style={ring} />
      {/* expanded interactive hit target extending outward around the ring for easy mobile tapping/dragging */}
      <div
        role="slider"
        aria-label="Hue"
        aria-valuemin={0}
        aria-valuemax={360}
        aria-valuenow={value === null ? undefined : Math.round(value)}
        tabIndex={0}
        className="absolute cursor-crosshair touch-none outline-none"
        style={{ inset: -TOUCH_SLOP }}
        onPointerDown={(e) => {
          e.preventDefault() // stops the press from starting a text selection
          draggingRef.current = true
          e.currentTarget.setPointerCapture(e.pointerId)
          onChange(hueAt(e))
        }}
        onPointerMove={(e) => {
          const hue = hueAt(e)
          onHover?.(hue)
          if (draggingRef.current || e.buttons) onChange(hue)
        }}
        onPointerUp={() => {
          draggingRef.current = false
        }}
        onPointerCancel={() => {
          draggingRef.current = false
        }}
        onLostPointerCapture={() => {
          draggingRef.current = false
        }}
        onPointerLeave={() => onHover?.(null)}
        onKeyDown={(e) => {
          const step = { ArrowRight: 5, ArrowUp: 5, ArrowLeft: -5, ArrowDown: -5 }[e.key]
          if (!step) return
          e.preventDefault()
          onChange(((value ?? 0) + step + 360) % 360)
        }}
      />
      {/* relative so it paints above the absolutely positioned band and catches inner card clicks */}
      <div className="relative">{children}</div>
      {thumbPos && (
        <div
          className="pointer-events-none absolute rounded-full border-2 border-white shadow-[0_0_8px_rgba(0,0,0,0.8)]"
          style={{ width: thumb, height: thumb, ...thumbPos, background: hueToHex(value!) }}
        />
      )}
    </div>
  )
}
