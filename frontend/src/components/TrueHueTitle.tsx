import { useEffect, useState } from 'react'
import { hueToHex } from '@/components/SquareHueSlider'

interface TrueHueTitleProps {
  /**
   * When true (loading/waiting screens), overlaps "True" and "hue" and
   * slowly colour-shifts between showing "True" and showing "hue".
   * When false, renders the static "True Hue" title.
   */
  overlapAnimate?: boolean
  /** Current spinning hue angle (0-360) so the shift follows the active colour cycle */
  hueAngle?: number
  compact?: boolean
}

type ShiftStage = 'true' | 'shift-to-hue' | 'hue' | 'shift-to-true'

export function TrueHueTitle({
  overlapAnimate = false,
  hueAngle,
  compact = false,
}: TrueHueTitleProps) {
  const [stage, setStage] = useState<ShiftStage>('true')

  useEffect(() => {
    if (!overlapAnimate) {
      return
    }

    // Slow, smooth cycle: hold "True" (3.6s) -> shift (1.4s) -> hold "hue" (3.6s) -> shift (1.4s)
    const schedule: { next: ShiftStage; durationMs: number }[] = [
      { next: 'shift-to-hue', durationMs: 3600 },
      { next: 'hue', durationMs: 1400 },
      { next: 'shift-to-true', durationMs: 3600 },
      { next: 'true', durationMs: 1400 },
    ]

    let stepIndex = 0
    let timerId = 0

    const advance = () => {
      const step = schedule[stepIndex]
      setStage(step.next)
      stepIndex = (stepIndex + 1) % schedule.length
      timerId = window.setTimeout(advance, schedule[stepIndex].durationMs)
    }

    timerId = window.setTimeout(advance, schedule[0].durationMs)
    return () => window.clearTimeout(timerId)
  }, [overlapAnimate])

  const sizeClass = compact
    ? 'text-3xl sm:text-4xl leading-none'
    : 'text-5xl sm:text-6xl leading-none'

  if (!overlapAnimate) {
    return (
      <h1
        className={`w-max pl-[0.05em] font-jersey ${sizeClass} font-semibold tracking-wider text-center text-(--hue) [text-shadow:0_0_10px_#000,0_0_20px_#000,0_0_32px_#000]`}
      >
        True Hue
      </h1>
    )
  }

  const isTrueVisible =
    stage === 'true' || stage === 'shift-to-hue' || stage === 'shift-to-true'
  const isHueVisible =
    stage === 'hue' || stage === 'shift-to-hue' || stage === 'shift-to-true'
  const isShifting = stage === 'shift-to-hue' || stage === 'shift-to-true'
  const isHueDominant = stage === 'hue' || stage === 'shift-to-hue'

  // Follow the active spinning hue closely with gentle neighbouring offsets during shifts
  const leadColor =
    hueAngle !== undefined ? hueToHex((hueAngle + 35) % 360) : 'var(--hue)'
  const trailColor =
    hueAngle !== undefined
      ? hueToHex((hueAngle - 35 + 360) % 360)
      : 'var(--hue)'
  const midColor =
    hueAngle !== undefined ? hueToHex((hueAngle + 18) % 360) : 'var(--hue)'

  const tColor =
    stage === 'shift-to-hue'
      ? leadColor
      : stage === 'shift-to-true'
        ? trailColor
        : 'var(--hue)'
  const rColor = stage === 'shift-to-hue' ? leadColor : 'var(--hue)'
  const hColor =
    stage === 'shift-to-hue'
      ? trailColor
      : stage === 'shift-to-true'
        ? leadColor
        : 'var(--hue)'
  const ueColor = isShifting ? midColor : 'var(--hue)'

  return (
    <h1
      aria-label="True Hue"
      className={`relative inline-flex items-baseline justify-center font-jersey ${sizeClass} font-semibold tracking-wide select-none transition-transform duration-1400 ease-in-out ${
        isHueDominant ? '-translate-x-[0.22em]' : 'translate-x-0'
      }`}
    >
      {/* "T" — only part of "True"; colour-shifts along the active hue & fades when "hue" is showing */}
      <span
        aria-hidden="true"
        style={{ color: tColor }}
        className={`inline-block transition-[opacity,transform] duration-1400 ease-in-out [text-shadow:0_0_10px_#000,0_0_20px_#000,0_0_32px_#000] ${
          stage === 'true'
            ? 'opacity-100 translate-y-0'
            : stage === 'hue'
              ? 'opacity-0 -translate-y-1 pointer-events-none'
              : 'opacity-70'
        }`}
      >
        T
      </span>

      {/* Overlapping "r" / "h" slot:
          - "r" hides within the lowercase "h" when "hue" is showing
          - the top ascender (and right leg) of "h" disappears when "True" is showing */}
      <span
        aria-hidden="true"
        className={`relative inline-grid items-baseline transition-[margin] duration-1400 ease-in-out ${
          isHueDominant ? 'mr-0' : '-mr-[0.04em]'
        }`}
      >
        {/* Lowercase "r" (core of "True", sits inside lowercase "h") */}
        <span
          style={{ color: rColor }}
          className={`col-start-1 row-start-1 justify-self-start transition-opacity duration-1400 ease-in-out [text-shadow:0_0_10px_#000,0_0_20px_#000,0_0_32px_#000] ${
            isTrueVisible ? 'opacity-100' : 'opacity-0'
          }`}
        >
          r
        </span>

        {/* Lowercase "h" (overlays "r"; top part of "h" clips/disappears when "True" is showing) */}
        <span
          style={{ color: hColor }}
          className={`col-start-1 row-start-1 justify-self-start transition-[opacity,clip-path] duration-1400 ease-in-out [text-shadow:0_0_10px_#000,0_0_20px_#000,0_0_32px_#000] ${
            stage === 'hue'
              ? 'opacity-100 [clip-path:inset(0%_0%_0%_0%)]'
              : stage === 'true'
                ? 'opacity-0 [clip-path:inset(42%_34%_0%_0%)]'
                : 'opacity-85 [clip-path:inset(14%_12%_0%_0%)]'
          } ${isHueVisible ? '' : 'pointer-events-none'}`}
        >
          h
        </span>
      </span>

      {/* Shared "ue" suffix — follows the active rainbow colour in real time */}
      <span
        aria-hidden="true"
        style={{ color: ueColor }}
        className="inline-block [text-shadow:0_0_10px_#000,0_0_20px_#000,0_0_32px_#000]"
      >
        ue
      </span>
    </h1>
  )
}
