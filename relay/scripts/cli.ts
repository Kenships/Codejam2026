/**
 * Shared bits for the fake-host / fake-player command-line clients.
 * These scripts are dev tools: they talk to a running relay exactly like
 * Unity or a phone would, so you can poke at it by hand.
 */
import readline from "node:readline";
import { styleText } from "node:util";

/** Where to connect: RELAY_URL, or the local dev relay. */
export const DEFAULT_URL = process.env.RELAY_URL ?? "ws://localhost:8080";

type Kind = "in" | "out" | "info" | "error";

const STYLE: Record<Kind, Parameters<typeof styleText>[0]> = {
    in: "cyan",
    out: "gray",
    info: "green",
    error: "red",
};
const ARROW: Record<Kind, string> = { in: "←", out: "→", info: "•", error: "!" };

let rl: readline.Interface | undefined;

/** Print a line without mangling what the user is halfway through typing. */
export function log(kind: Kind, text: string) {
    const line = `${ARROW[kind]} ${text}`;
    const styled = process.stdout.isTTY ? styleText(STYLE[kind], line) : line;
    if (rl && process.stdout.isTTY) {
        readline.clearLine(process.stdout, 0);
        readline.cursorTo(process.stdout, 0);
        console.log(styled);
        rl.prompt(true);
    } else {
        console.log(styled);
    }
}

/** What each WebSocket close code means, in plain words. */
export const CLOSE_MEANINGS: Record<number, string> = {
    1000: "normal close",
    1001: "relay shutting down",
    1005: "closed (no code)",
    1006: "connection lost",
    1008: "origin not allowed",
    1009: "message too big",
    4000: "kicked by the host",
    4002: "replaced by a newer connection with the same playerId",
};

/**
 * Turn what the user typed into a game payload `d`:
 *   state                     → {"t":"state"}
 *   {"t":"guess","hex":"#F00"} → that object
 * Returns null (and prints why) if it isn't valid.
 */
export function parsePayload(text: string): Record<string, unknown> | null {
    const trimmed = text.trim();
    if (trimmed === "") {
        log("error", "missing payload: give a word like `state` or a JSON object");
        return null;
    }
    if (!trimmed.startsWith("{")) return { t: trimmed };
    try {
        const value: unknown = JSON.parse(trimmed);
        if (typeof value === "object" && value !== null && !Array.isArray(value)) {
            return value as Record<string, unknown>;
        }
    } catch {
        // fall through
    }
    log("error", `not a JSON object: ${trimmed}`);
    return null;
}

/** Split "to Harry {...}" into ["to", "Harry", "{...}"], keeping the JSON intact. */
export function splitCommand(line: string, words: number): string[] {
    const parts: string[] = [];
    let rest = line.trim();
    for (let i = 0; i < words - 1; i++) {
        const space = rest.search(/\s/);
        if (space === -1) break;
        parts.push(rest.slice(0, space));
        rest = rest.slice(space).trim();
    }
    parts.push(rest);
    return parts;
}

/** Start the interactive prompt. `onLine` gets each typed line. Ctrl+D or `quit` exits. */
export function startPrompt(label: string, help: string, onLine: (line: string) => void) {
    rl = readline.createInterface({ input: process.stdin, output: process.stdout, prompt: `${label}> ` });
    console.log(help);
    rl.prompt();
    rl.on("line", (line) => {
        const trimmed = line.trim();
        if (trimmed === "quit" || trimmed === "exit") return rl?.close();
        if (trimmed === "help") console.log(help);
        else if (trimmed !== "") onLine(trimmed);
        rl?.prompt();
    });
    rl.on("close", () => process.exit(0));
}
