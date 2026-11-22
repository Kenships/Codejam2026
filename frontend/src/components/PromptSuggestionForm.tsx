import { useState, type FormEvent } from 'react'
import { Check } from 'lucide-react'

import { Button } from '@/components/ui/button'
import { PROMPT_SUGGESTION_MAX_LENGTH } from '@/lib/protocol'
import { relayClient } from '@/lib/relayClient'
import { useLoginStore } from '@/store/useLoginStore'

/** Collapse whitespace and cap the length, matching what the host accepts (PROTOCOL.md §7). */
function cleanSuggestion(text: string): string {
  return text.replace(/\s+/g, ' ').trim().slice(0, PROMPT_SUGGESTION_MAX_LENGTH)
}

/**
 * Lobby row for optionally suggesting a category (PROTOCOL.md §7 `prompt_suggest`). The host
 * deals it to exactly one other artist as one of their 4 choices. Submitting again replaces
 * the suggestion; submitting it empty withdraws it. Styled like the drawing card's prompt row.
 */
export function PromptSuggestionForm() {
  const saved = useLoginStore((s) => s.promptSuggestion)
  const setSaved = useLoginStore((s) => s.setPromptSuggestion)
  const [draft, setDraft] = useState(saved)

  const cleaned = cleanSuggestion(draft)
  const unchanged = cleaned === saved

  const handleSubmit = (e: FormEvent) => {
    e.preventDefault()
    if (unchanged) return
    relayClient.send('host', { t: 'prompt_suggest', text: cleaned })
    setSaved(cleaned)
    setDraft(cleaned)
  }

  return (
    <form onSubmit={handleSubmit} className="flex w-full flex-col gap-1">
      <label htmlFor="prompt-suggestion" className="text-sm text-(--hue)/80">
        Suggest a category for someone else to draw (optional)
      </label>
      <div className="flex gap-2">
        <input
          id="prompt-suggestion"
          type="text"
          placeholder="e.g. Things in a Fridge"
          maxLength={PROMPT_SUGGESTION_MAX_LENGTH}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          className="min-w-0 flex-1 border-2 border-(--hue) font-jersey bg-black/50 px-2.5 py-1.5 text-base text-(--hue) placeholder:text-(--hue)/60"
        />
        <Button
          type="submit"
          disabled={unchanged}
          className="shrink-0 transition-none select-none bg-alpha text-(--hue) border-2 border-(--hue) font-jersey px-3 py-1.5 hover:bg-(--hue) hover:text-black disabled:opacity-100"
        >
          {unchanged && saved ? (
            <>
              <Check className="h-4 w-4" /> Sent
            </>
          ) : saved && !cleaned ? (
            'Remove'
          ) : (
            'Suggest'
          )}
        </Button>
      </div>
    </form>
  )
}
