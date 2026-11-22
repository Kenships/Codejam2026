import { cn } from '@/lib/utils'

/**
 * Trailing "..." whose dots hop one after another, like a typing indicator.
 * Matches the Unity host's WaveText dots (1.2 s cycle, 0.15 s stagger, 0.28em hop);
 * the keyframes live in index.css (.bouncing-dot). Use in place of a literal "...":
 *   Waiting for players<BouncingDots />
 */
export function BouncingDots({ className }: { className?: string }) {
  return (
    <span aria-hidden="true" className={cn('inline-flex', className)}>
      {[0, 1, 2].map((i) => (
        <span
          key={i}
          className="bouncing-dot"
          style={{ animationDelay: `${i * 0.15}s` }}
        >
          .
        </span>
      ))}
    </span>
  )
}
