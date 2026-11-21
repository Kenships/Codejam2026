import { describe, test } from "node:test";
import assert from "node:assert/strict";
import { TokenBucket } from "../src/rateLimit.ts";

/** Call take() n times at the same instant; return how many succeeded. */
function takeMany(bucket: TokenBucket, n: number, now: number) {
    let ok = 0;
    for (let i = 0; i < n; i++) if (bucket.take(now)) ok++;
    return ok;
}

describe("TokenBucket", () => {
    test("starts full: a burst up to capacity is allowed, then refused", () => {
        const b = new TokenBucket(5, 5, 0);
        assert.equal(takeMany(b, 8, 0), 5);
    });

    test("refills over time at perSecond", () => {
        const b = new TokenBucket(5, 10, 0);
        takeMany(b, 5, 0); // empty
        assert.equal(b.take(0), false);
        assert.equal(takeMany(b, 10, 300), 3, "0.3 s at 10/s = 3 tokens");
    });

    test("never refills past capacity, however long it waits", () => {
        const b = new TokenBucket(5, 10, 0);
        takeMany(b, 5, 0);
        assert.equal(takeMany(b, 100, 60_000), 5);
    });

    test("a steady rate at or below perSecond is never refused", () => {
        const b = new TokenBucket(40, 40, 0);
        // 10 messages per second (an artist's stroke batches) for a minute.
        for (let t = 0; t < 60_000; t += 100) assert.equal(b.take(t), true, `refused at ${t} ms`);
    });

    test("fractional refill accumulates", () => {
        const b = new TokenBucket(1, 10, 0);
        b.take(0);
        assert.equal(b.take(50), false, "0.05 s = half a token");
        assert.equal(b.take(100), true, "another 0.05 s = a whole token");
    });
});
