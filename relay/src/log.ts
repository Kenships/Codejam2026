/**
 * Minimal leveled logger: one line per event, `key=value` fields.
 *
 *   2026-11-21T05:12:03.120Z INFO  player joined conn=7 room=KXQM name=Harry
 *
 * LOG_LEVEL picks the minimum level printed: debug | info | warn | error | silent.
 * Never log the hostKey: whoever has it can take over a room.
 */
import { config } from "./config.ts";

const LEVELS = { debug: 10, info: 20, warn: 30, error: 40, silent: 100 } as const;
export type LogLevel = keyof typeof LEVELS;

type Fields = Record<string, string | number | boolean | null | undefined>;

function isLogLevel(value: string): value is LogLevel {
    return value in LEVELS;
}

let threshold: number = LEVELS[isLogLevel(config.logLevel) ? config.logLevel : "info"];

/** Change the level at runtime (tests use "silent"). */
export function setLogLevel(level: LogLevel) {
    threshold = LEVELS[level];
}

function formatValue(value: string | number | boolean | null): string {
    // Quote strings that contain spaces or quotes so each field stays one token.
    if (typeof value === "string" && /[\s"=]/.test(value)) return JSON.stringify(value);
    return String(value);
}

function write(level: Exclude<LogLevel, "silent">, message: string, fields?: Fields, err?: unknown) {
    if (LEVELS[level] < threshold) return;
    const parts = [new Date().toISOString(), level.toUpperCase().padEnd(5), message];
    for (const [key, value] of Object.entries(fields ?? {})) {
        if (value !== undefined) parts.push(`${key}=${formatValue(value)}`);
    }
    const out = level === "warn" || level === "error" ? console.error : console.log;
    out(parts.join(" "));
    if (err !== undefined) out(err instanceof Error ? err.stack ?? err.message : String(err));
}

export const log = {
    debug: (message: string, fields?: Fields) => write("debug", message, fields),
    info: (message: string, fields?: Fields) => write("info", message, fields),
    warn: (message: string, fields?: Fields) => write("warn", message, fields),
    /** Pass the caught error as `err` to print its stack trace. */
    error: (message: string, fields?: Fields, err?: unknown) => write("error", message, fields, err),
};

if (!isLogLevel(config.logLevel)) {
    log.warn("unknown LOG_LEVEL, using info", { LOG_LEVEL: config.logLevel });
}
