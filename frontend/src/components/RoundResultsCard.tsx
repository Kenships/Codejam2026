import type { CSSProperties } from 'react'
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from '@/components/ui/card'
import {
  formatOrdinal,
  getResultText,
  type PlayerRoundResult,
} from '@/lib/results'

interface RoundResultsCardProps {
  username?: string
  result: PlayerRoundResult
  pickedHex: string
}

export function RoundResultsCard({
  username,
  result,
  pickedHex,
}: RoundResultsCardProps) {
  const { roundPoints, totalPoints, roundRank, overallRank, totalPlayers } =
    result

  const formatRank = (rank: number) =>
    totalPlayers
      ? `${formatOrdinal(rank)} / ${totalPlayers}`
      : formatOrdinal(rank)

  const resultLabel = getResultText(roundPoints)

  return (
    <Card
      className="w-full select-none bg-black/80 ring-0 border-2 border-(--hue) font-jersey text-(--picked)"
      style={{ '--picked': pickedHex } as CSSProperties}
    >
      <CardHeader className="pb-2">
        <CardTitle className="font-jersey sm:text-4xl text-3xl text-center text-(--picked) [text-shadow:0_0_8px_#000,0_0_16px_#000]">
          {resultLabel}
        </CardTitle>
        {username && (
          <CardDescription className="font-jersey text-center sm:text-base text-sm text-(--picked)/80 [text-shadow:0_0_6px_#000,0_0_12px_#000]">
            {username}
          </CardDescription>
        )}
      </CardHeader>

      <CardContent className="flex flex-col gap-3 pb-5">
        <div className="grid grid-cols-2 divide-x-2 divide-(--hue)/40 rounded border-2 border-(--hue) bg-black/60 py-4 text-center">
          <div className="flex flex-col items-center px-3">
            <span className="text-xs uppercase tracking-wider text-(--picked)/70">
              Round Points
            </span>
            <span className="text-3xl sm:text-4xl font-semibold tabular-nums text-(--picked)">
              +{roundPoints.toLocaleString()}
            </span>
          </div>

          <div className="flex flex-col items-center px-3">
            <span className="text-xs uppercase tracking-wider text-(--picked)/70">
              Round Rank
            </span>
            <span className="text-3xl sm:text-4xl font-semibold tabular-nums text-(--picked)">
              {formatRank(roundRank)}
            </span>
          </div>
        </div>

        <div className="grid grid-cols-2 divide-x-2 divide-(--hue)/40 rounded border-2 border-(--hue)/60 bg-black/50 py-3 text-center">
          <div className="flex flex-col items-center px-3">
            <span className="text-xs uppercase tracking-wider text-(--picked)/70">
              Total Points
            </span>
            <span className="text-2xl sm:text-3xl font-semibold tabular-nums text-(--picked)">
              {totalPoints.toLocaleString()}
            </span>
          </div>

          <div className="flex flex-col items-center px-3">
            <span className="text-xs uppercase tracking-wider text-(--picked)/70">
              Overall Rank
            </span>
            <span className="text-2xl sm:text-3xl font-semibold tabular-nums text-(--picked)">
              {formatRank(overallRank)}
            </span>
          </div>
        </div>
      </CardContent>
    </Card>
  )
}
