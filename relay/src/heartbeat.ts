import { WebSocket, type WebSocketServer } from "ws";
import { log } from "./log.ts";

/**
 * Detect sockets that died without saying goodbye (phone out of signal,
 * battery died, laptop lid closed). Their TCP connection can look open
 * for minutes, so we ask: every intervalMs, ping each socket; if it hasn't
 * answered the previous ping by the next tick, terminate it.
 *
 * Terminating fires the socket's normal "close" event, so connection.ts
 * treats it like any other drop (grace timer, player_disconnected, ...).
 *
 * Clients do nothing: browsers and NativeWebSocket answer pings automatically.
 *
 * Returns a function that stops the heartbeat.
 */
export function startHeartbeat(wss: WebSocketServer, intervalMs: number): () => void {
    // Did this socket answer since the last tick? A WeakMap lets us attach
    // that flag to each socket without changing the ws types, and entries
    // vanish automatically once a socket is garbage-collected.
    const answered = new WeakMap<WebSocket, boolean>();

    wss.on("connection", (ws: WebSocket) => {
        answered.set(ws, true);
        ws.on("pong", () => answered.set(ws, true));
    });

    const timer = setInterval(() => {
        for (const ws of wss.clients) {
            // ping() throws on a socket that's already closing.
            if (ws.readyState !== WebSocket.OPEN) continue;

            if (answered.get(ws) === false) {
                log.debug("heartbeat: no pong, terminating socket");
                ws.terminate(); // no reply since last tick: treat as dead
                continue;
            }
            answered.set(ws, false);
            ws.ping();
        }
    }, intervalMs);

    return () => clearInterval(timer);
}
