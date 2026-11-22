export interface PlayerRoundResult {
  roundPoints: number
  totalPoints: number
  roundRank: number
  overallRank: number
  totalPlayers?: number
  /** Exact target colour hex returned by the backend (e.g. "#FF5733") */
  targetHex: string
  /** True when the current player was the artist for this round */
  isArtist?: boolean
}

export type RoundOutcome = 'correct' | 'close' | 'poor' | 'none'

export const RESULT_TEXT: Record<RoundOutcome, string> = {
  correct: 'Correct! You guessed the right color!',
  close: 'Close! Your color was near the target.',
  poor: 'Poor! Your color was far from the target.',
  none: 'No points. Your color was far from the target.',
}

export const ARTIST_RESULT_TEXT: Record<RoundOutcome, string> = {
  correct: 'Masterpiece! Players nailed your colour!',
  close: 'Nice Clue! Players got close to your colour.',
  poor: 'Weak Clue! Players missed your colour.',
  none: 'Terrible Clue! Players missed your colour.',
}

export function getRoundOutcome(points: number): RoundOutcome {
  if (points >= 750) return 'correct'
  if (points >= 250) return 'close'
  if (points > 0) return 'poor'
  return 'none'
}

export function getResultText(points: number): string {
  return RESULT_TEXT[getRoundOutcome(points)]
}

export function getArtistResultText(points: number): string {
  return ARTIST_RESULT_TEXT[getRoundOutcome(points)]
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
