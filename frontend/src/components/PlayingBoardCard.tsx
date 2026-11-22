import { useEffect, useState } from 'react'
import { Check, RotateCcw } from 'lucide-react'

import { TutorialTabs } from '@/components/HowToPlay'
import { SquareHueSlider, hueToHex } from '@/components/SquareHueSlider'
import { BouncingDots } from '@/components/ui/bouncing-dots'
import { Button } from '@/components/ui/button'
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from '@/components/ui/card'
import { decompressDrawingImageToDataUrl, type DrawingMime } from '@/lib/drawingCodec'
import { relayClient } from '@/lib/relayClient'

export interface IncomingRoundDrawing {
  /** GZIP-compressed Base64 image received from the relay/host */
  imageBase64: string
  mime?: DrawingMime
  prompt: string
  artistName?: string
}

// Decompresses a round drawing and shows its white strokes tinted to `color`
export function DrawingViewport({
  drawing,
  color,
}: {
  drawing: IncomingRoundDrawing | null
  color: string
}) {
  const [decompressedState, setDecompressedState] = useState<{
    sourceBase64: string
    url: string
  } | null>(null)

  const imageBase64 = drawing?.imageBase64 ?? ''
  const mime = drawing?.mime ?? 'image/png'

  const decompressedUrl =
    decompressedState?.sourceBase64 === imageBase64
      ? decompressedState.url
      : ''
  const isDecompressing =
    Boolean(imageBase64) && decompressedState?.sourceBase64 !== imageBase64

  // Decompress incoming GZIP+Base64 image whenever a new round drawing arrives
  useEffect(() => {
    if (!imageBase64) return

    let cancelled = false

    decompressDrawingImageToDataUrl(imageBase64, mime)
      .then((url) => {
        if (!cancelled) {
          setDecompressedState({ sourceBase64: imageBase64, url })
        }
      })
      .catch(() => {
        if (!cancelled) {
          setDecompressedState({ sourceBase64: imageBase64, url: '' })
        }
      })

    return () => {
      cancelled = true
    }
  }, [imageBase64, mime])

  return (
    <div className="relative mx-auto aspect-square w-full max-w-[min(100%,30dvh,320px)] overflow-hidden border-2 border-(--hue) bg-black flex items-center justify-center">
      {isDecompressing ? (
        <span className="text-base text-(--hue)/70">
          Decompressing drawing<BouncingDots />
        </span>
      ) : decompressedUrl ? (
        <>
          <img
            src={decompressedUrl}
            alt={drawing?.prompt || 'Artist clue drawing'}
            draggable={false}
            className="block h-full w-full object-contain select-none"
          />
          {/* Multiply blend turns #ffffff strokes into `color` while keeping #000000 black */}
          <div
            aria-hidden="true"
            className="pointer-events-none absolute inset-0 mix-blend-multiply"
            style={{ backgroundColor: color }}
          />
        </>
      ) : (
        <span className="px-4 text-center text-base text-(--hue)/70">
          Waiting for artist drawing<BouncingDots />
        </span>
      )}
    </div>
  )
}

interface PlayingBoardCardProps {
  hue: number | null
  onHueChange: (hue: number) => void
  onResetHue: () => void
  drawing: IncomingRoundDrawing | null
  /** Force submitted/waiting state for dev preview */
  forceSubmitted?: boolean
  onGuessSubmitted?: (guessedHex: string) => void
}

export function PlayingBoardCard({
  hue,
  onHueChange,
  onResetHue,
  drawing,
  forceSubmitted = false,
  onGuessSubmitted,
}: PlayingBoardCardProps) {
  const [submittedGuess, setSubmittedGuess] = useState<{
    sourceBase64: string
    hex: string
  } | null>(null)

  const pickedColor = hue === null ? '#ffffff' : hueToHex(hue)
  const imageBase64 = drawing?.imageBase64 ?? ''

  const submittedHex =
    submittedGuess?.sourceBase64 === imageBase64 ? submittedGuess.hex : null
  const isSubmitted = submittedHex !== null || forceSubmitted
  const displayColor = submittedHex ?? pickedColor

  const handleHueChange = (nextHue: number) => {
    if (isSubmitted) return
    onHueChange(nextHue)
  }

  const handleResetHue = () => {
    if (isSubmitted) return
    onResetHue()
  }

  const handleSubmitGuess = () => {
    if (isSubmitted) return
    // PROTOCOL.md §7: guess | player -> host | hex
    relayClient.send('host', {
      t: 'guess',
      hex: pickedColor,
    })
    setSubmittedGuess({ sourceBase64: imageBase64, hex: pickedColor })
    onGuessSubmitted?.(pickedColor)
  }

  return (
    <SquareHueSlider value={hue} onChange={handleHueChange}>
      <Card className="w-full select-none bg-black/85 ring-0 border-0 font-jersey text-(--hue)">
        <CardHeader className="pb-2 pt-3 px-3 sm:px-4">
          <CardTitle className="font-jersey sm:text-2xl text-xl text-center text-(--hue) [text-shadow:0_0_8px_#000,0_0_16px_#000]">
            {drawing?.prompt ? `"${drawing.prompt}"` : 'Guess the Colour!'}
          </CardTitle>
          <CardDescription className="font-jersey text-center sm:text-sm text-xs text-(--hue)/80 [text-shadow:0_0_6px_#000]">
            {isSubmitted
              ? 'Guess locked in!'
              : drawing?.artistName
                ? `Drawn by ${drawing.artistName} — drag the square border to match the hue`
                : 'Drag the square border to match the hue'}
          </CardDescription>
        </CardHeader>

        <CardContent className="flex flex-col items-center gap-3 px-3 sm:px-4 pb-4">
          {isSubmitted ? (
            <>
              <TutorialTabs label="Your Guess">
                <DrawingViewport drawing={drawing} color={displayColor} />
              </TutorialTabs>
              <div className="flex w-full flex-col items-center gap-1 border-2 border-(--hue) bg-black/60 px-3 py-2.5 text-center">
                <div className="flex items-center gap-1.5">
                  <Check className="h-4 w-4 text-(--hue)" />
                  <span className="text-base sm:text-lg text-(--hue)">
                    Wait for other players to submit<BouncingDots />
                  </span>
                </div>
              </div>
            </>
          ) : (
            <DrawingViewport drawing={drawing} color={displayColor} />
          )}

          {!isSubmitted && (
            <div className="flex w-full items-center justify-between gap-2">
              <Button
                type="button"
                disabled={hue === null}
                onClick={handleResetHue}
                title="Reset back to white"
                className="transition-none select-none bg-alpha text-(--hue) border-2 border-(--hue) font-jersey px-4 py-1.5 hover:bg-(--hue) hover:text-black"
              >
                <RotateCcw className="h-3 w-3" />
                <span>Reset</span>
              </Button>
              <Button
                type="button"
                onClick={handleSubmitGuess}
                className="transition-none select-none bg-alpha text-(--hue) border-2 border-(--hue) font-jersey px-4 py-1.5 hover:bg-(--hue) hover:text-black"
              >
                Submit
              </Button>
            </div>
          )}
        </CardContent>
      </Card>
    </SquareHueSlider>
  )
}
