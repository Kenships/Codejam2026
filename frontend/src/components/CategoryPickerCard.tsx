import { useEffect, useState } from 'react'

import { Button } from '@/components/ui/button'
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from '@/components/ui/card'

interface CategoryPickerCardProps {
  /** Exact target colour hex received from the host (e.g. "#3FA7D6") */
  targetHex: string
  /** Category choices offered by the host (PROTOCOL.md §7: 4 strings) */
  categories: string[]
  /** Optional local deadline timestamp (ms) derived from state.remainingMs - ageMs */
  deadlineMs?: number | null
  /** Called when the player taps a category */
  onPick: (category: string) => void
}

export function CategoryPickerCard({
  targetHex,
  categories,
  deadlineMs = null,
  onPick,
}: CategoryPickerCardProps) {
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    if (deadlineMs === null) return

    const syncId = window.setTimeout(() => setNow(Date.now()), 0)
    const intervalId = window.setInterval(() => {
      setNow(Date.now())
    }, 250)
    return () => {
      window.clearTimeout(syncId)
      window.clearInterval(intervalId)
    }
  }, [deadlineMs])

  const secondsLeft =
    deadlineMs !== null
      ? Math.max(0, Math.ceil((deadlineMs - now) / 1000))
      : null

  const isUrgent = secondsLeft !== null && secondsLeft <= 10

  return (
    <Card className="w-full select-none bg-black/80 ring-0 border-2 border-(--hue) font-jersey text-(--hue)">
      <CardHeader className="pb-2 pt-3 px-3 sm:px-4">
        <div className="flex items-center justify-between gap-2">
          <div className="flex items-center gap-2 min-w-0">
            <span
              className="h-6 w-6 shrink-0 rounded border-2 border-white/80"
              style={{ backgroundColor: targetHex }}
              aria-label={`Target colour ${targetHex}`}
            />
            <div className="flex flex-col leading-tight min-w-0">
              <CardTitle className="font-jersey text-lg sm:text-xl text-(--hue) [text-shadow:0_0_8px_#000]">
                Pick a Category
              </CardTitle>
              <span className="text-xs tracking-wider uppercase text-(--hue)/75">
                Target: {targetHex.toUpperCase()}
              </span>
            </div>
          </div>

          {secondsLeft !== null && (
            <span
              className={`rounded border-2 px-2.5 py-0.5 text-lg sm:text-xl tabular-nums ${
                isUrgent
                  ? 'border-red-400 text-red-400 animate-pulse'
                  : 'border-(--hue) text-(--hue)'
              }`}
            >
              {secondsLeft}s
            </span>
          )}
        </div>

        <CardDescription className="pt-1 font-jersey text-sm sm:text-base text-(--hue)/80 [text-shadow:0_0_6px_#000]">
          Choose what you&apos;ll draw to hint at your colour
        </CardDescription>
      </CardHeader>

      <CardContent className="px-3 sm:px-4 pb-4">
        <div className="grid grid-cols-2 gap-2.5">
          {categories.map((choice) => (
            <Button
              key={choice}
              type="button"
              onClick={() => onPick(choice)}
              className="h-auto min-h-16 whitespace-normal px-3 py-3 text-lg sm:text-xl transition-none select-none bg-alpha text-(--hue) border-2 border-(--hue) font-jersey hover:bg-(--hue) hover:text-black cursor-pointer"
            >
              {choice}
            </Button>
          ))}
        </div>
      </CardContent>
    </Card>
  )
}
