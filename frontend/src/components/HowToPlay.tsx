import { useEffect, useState, type ReactNode } from 'react'

const CYCLE_MS = 6000

const GUIDES = [
  {
    title: 'How to Draw',
    steps: [
      'You get a secret target colour.',
      'Pick a category, then draw something that hints at your colour.',
      'Add a clue and submit before the timer runs out.',
    ],
  },
  {
    title: 'How to Guess',
    steps: [
      "Look at another player's drawing and clue.",
      'Drag the square border until the hue matches.',
      'Submit. Closer guesses score more, and so does the artist!',
    ],
  },
]

// shown on waiting screens; cycles between the draw and guess guides
export function HowToPlay() {
  const [index, setIndex] = useState(0)

  // depends on index so tapping a dot restarts the countdown
  useEffect(() => {
    const id = setTimeout(() => setIndex((i) => (i + 1) % GUIDES.length), CYCLE_MS)
    return () => clearTimeout(id)
  }, [index])

  return (
    <div className="w-full border-2 border-(--hue)/60 bg-black/60 px-3 py-2 text-left text-(--hue)">
      {/* both guides share one grid cell: the box keeps the taller one's height and they crossfade */}
      <div className="grid">
        {GUIDES.map((guide, i) => (
          <div
            key={guide.title}
            aria-hidden={i !== index}
            className={`[grid-area:1/1] transition-opacity duration-500 motion-reduce:transition-none ${
              i === index ? 'opacity-100' : 'opacity-0'
            }`}
          >
            <h3 className="mb-1 text-lg sm:text-xl tracking-wider uppercase">{guide.title}</h3>
            <ol className="flex flex-col gap-0.5 text-sm sm:text-base text-(--hue)/85">
              {guide.steps.map((step, n) => (
                <li key={step} className="flex gap-2">
                  <span className="shrink-0">{n + 1}.</span>
                  <span>{step}</span>
                </li>
              ))}
            </ol>
          </div>
        ))}
      </div>
      <div className="mt-2 flex justify-center gap-2">
        {GUIDES.map((guide, i) => (
          <button
            key={guide.title}
            type="button"
            aria-label={`Show ${guide.title}`}
            aria-pressed={i === index}
            onClick={() => setIndex(i)}
            className={`h-2 w-6 cursor-pointer border border-(--hue) ${i === index ? 'bg-(--hue)' : 'bg-transparent'}`}
          />
        ))}
      </div>
    </div>
  )
}

// waiting-screen box: either `children` (avatar, drawing, guess...) or the How to Play guide
export function TutorialTabs({ label, children }: { label: string; children: ReactNode }) {
  const [showHelp, setShowHelp] = useState(false)
  const tabs = [
    { text: label, active: !showHelp, select: () => setShowHelp(false) },
    { text: 'How to Play', active: showHelp, select: () => setShowHelp(true) },
  ]

  return (
    <div className="flex w-full flex-col gap-2">
      <div role="tablist" className="grid grid-cols-2 border-2 border-(--hue)">
        {tabs.map((tab) => (
          <button
            key={tab.text}
            type="button"
            role="tab"
            aria-selected={tab.active}
            onClick={tab.select}
            className={`cursor-pointer px-1 py-1 font-jersey text-sm sm:text-lg leading-tight text-balance tracking-wide sm:tracking-wider uppercase ${
              tab.active ? 'bg-(--hue) text-black' : 'text-(--hue) hover:bg-(--hue)/20'
            }`}
          >
            {tab.text}
          </button>
        ))}
      </div>
      {/* both panels stay mounted in one grid cell: the box doesn't resize on switch,
          and the avatar canvas keeps its strokes */}
      <div className="grid">
        <div role="tabpanel" inert={showHelp} className={`[grid-area:1/1] flex justify-center ${showHelp ? 'invisible' : ''}`}>
          {children}
        </div>
        <div role="tabpanel" inert={!showHelp} className={`[grid-area:1/1] ${showHelp ? '' : 'invisible'}`}>
          <HowToPlay />
        </div>
      </div>
    </div>
  )
}
