export const config = {
    port: Number(process.env.PORT ?? 8080),
    allowedOrigins: (process.env.ALLOWED_ORIGINS)?.split(',').map(x => x.trim()).filter((x) => x) ?? [],
    logLevel: process.env.LOG_LEVEL ?? "info",
    maxPayloadBytes: 64 * 1024,

    /** How long things wait. Tests pass much smaller values to startRelay. */
    timings: {
        /** A dropped player keeps their seat this long. */
        playerGraceMs: 30_000,
        /** A dropped host keeps the room open this long. */
        hostGraceMs: 60_000,
        /** A room with no messages for this long is closed. */
        idleRoomMs: 10 * 60_000,
        /** How often to look for idle rooms. */
        sweepMs: 60_000,
        /** How often to ping every socket. Two missed intervals = terminated. */
        heartbeatMs: 25_000,
    },

    /**
     * Per socket. A token bucket: up to `burst` messages at once, refilling at
     * `perSecond`. The biggest burst is the host at a phase change: about two
     * messages per player (e.g. your_target + state), so ~64 with 32 players.
     */
    rateLimit: {
        burst: 100,
        perSecond: 40,
    },
};

export type Timings = typeof config.timings;
export type RateLimit = typeof config.rateLimit;