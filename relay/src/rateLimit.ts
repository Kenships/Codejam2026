/**
 * A token bucket: the standard way to limit a rate while still allowing
 * short bursts.
 *
 * The bucket holds up to `capacity` tokens and starts full. Each message
 * costs one token. Tokens refill continuously at `perSecond`. With no token
 * left, the message is refused.
 *
 * `now` is a parameter (defaulting to the real clock) so tests can move
 * time forward by hand instead of waiting.
 */
export class TokenBucket {
    private readonly capacity: number;
    private readonly perSecond: number;
    private tokens: number;
    private lastRefill: number;

    constructor(capacity: number, perSecond: number, now: number = Date.now()) {
        this.capacity = capacity;
        this.perSecond = perSecond;
        this.tokens = capacity;
        this.lastRefill = now;
    }

    /** Spend one token if there is one. Returns false if the caller is over the limit. */
    take(now: number = Date.now()): boolean {
        this.refill(now);
        if (this.tokens >= 1) {
            this.tokens -= 1;
            return true;
        }
        return false;
    }

    private refill(now: number) {
        const elapsedSeconds = (now - this.lastRefill) / 1000;
        this.tokens = Math.min(this.capacity, this.tokens + elapsedSeconds * this.perSecond);
        this.lastRefill = now;
    }
}
