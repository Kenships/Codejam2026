export interface PlayerRoundResult {
  roundPoints: number
  totalPoints: number
  roundRank: number
  overallRank: number
  totalPlayers?: number
  /** Exact target colour hex returned by the backend (e.g. "#FF5733") */
  targetHex: string
}

export type RoundOutcome = 'correct' | 'close' | 'none'

export const RESULT_TEXT: Record<RoundOutcome, string> = {
  correct: 'Correct! You guessed the right color!',
  close: 'Close! Your color was near the target.',
  none: 'No points. Your color was far from the target.',
}

export function getRoundOutcome(points: number): RoundOutcome {
  if (points >= 250) return 'correct'
  if (points >= 100) return 'close'
  return 'none'
}

export function getResultText(points: number): string {
  return RESULT_TEXT[getRoundOutcome(points)]
}

export function formatOrdinal(rank: number): string {
  const mod100 = rank % 100
  if (mod100 >= 11 && mod100 <= 13) return `${rank}th`
  switch (rank % 10) {
    case 1:
      return `${rank}st`
    case 2:
      return `${rank}nd`
    case 3:
      return `${rank}rd`
    default:
      return `${rank}th`
  }
}
