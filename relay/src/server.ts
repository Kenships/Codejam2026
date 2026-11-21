import http from "node:http";
import type { AddressInfo } from "node:net";
import { WebSocketServer } from "ws";
import { config, type RateLimit, type Timings } from "./config.ts";
import { Rooms } from "./rooms.ts";
import { handleConnection } from "./connection.ts";
import { startHeartbeat } from "./heartbeat.ts";
import { isOriginAllowed } from "./origin.ts";
import { log } from "./log.ts";

export type Relay = {
    /** The port actually bound (useful when you asked for port 0). */
    port: number;
    rooms: Rooms;
    /** Disconnect every client immediately and stop listening. */
    close(): Promise<void>;
    /**
     * Polite version of close(), for SIGTERM: tell every room
     * room_closed {server_shutdown}, give clients up to graceMs to finish
     * closing (so that message actually arrives), then close().
     */
    shutdown(graceMs?: number): Promise<void>;
};

export type RelayOptions = {
    /** 0 lets the OS pick a free port (tests). */
    port: number;
    /**
     * Interface to listen on. Leave undefined in production to listen on every
     * interface (Railway and phones on the LAN need that). Tests use "127.0.0.1".
     */
    host?: string;
    /** Override any of config.timings (tests use milliseconds). */
    timings?: Partial<Timings>;
    /** Defaults to config.allowedOrigins. Empty = allow any website. */
    allowedOrigins?: readonly string[];
    /** Defaults to config.rateLimit. */
    rateLimit?: RateLimit;
};

/** Build the HTTP + WebSocket server and start listening. */
export function startRelay(options: RelayOptions): Promise<Relay> {
    const { port, host } = options;
    const timings: Timings = { ...config.timings, ...options.timings };
    const allowedOrigins = options.allowedOrigins ?? config.allowedOrigins;
    const rateLimit = options.rateLimit ?? config.rateLimit;
    const rooms = new Rooms();

    const server = http.createServer((req, res) => {
        if (req.method === "GET" && req.url === "/healthz") {
            res.writeHead(200, { "Content-Type": "application/json" });
            res.end(JSON.stringify({ ok: true, uptime: process.uptime() }));
        } else {
            res.writeHead(404, { "Content-Type": "text/plain" });
            res.end("404 Not Found: The requested page does not exist.");
        }
    });

    const wss = new WebSocketServer({ noServer: true, maxPayload: config.maxPayloadBytes });

    server.on("upgrade", (req, socket, head) => {
        wss.handleUpgrade(req, socket, head, (ws) => {
            if (!isOriginAllowed(req.headers.origin, allowedOrigins)) {
                log.warn("origin rejected", { origin: req.headers.origin });
                // Finish the handshake, then close with a code + reason the
                // browser's onclose can show. A refused handshake (HTTP 403)
                // would only surface in the browser as a generic error.
                ws.close(1008, "origin not allowed");
                return;
            }
            wss.emit("connection", ws, req);
        });
    });

    wss.on("connection", (ws) => handleConnection(ws, rooms, timings, rateLimit));

    // Terminate sockets that stop answering pings.
    const stopHeartbeat = startHeartbeat(wss, timings.heartbeatMs);

    // Every sweepMs, close rooms nobody has sent anything to for idleRoomMs.
    const sweep = setInterval(() => rooms.closeIdleRooms(timings.idleRoomMs), timings.sweepMs);

    return new Promise((resolve, reject) => {
        server.once("error", reject);
        server.listen(port, host, () => {
            const bound = (server.address() as AddressInfo).port;

            const close = () =>
                new Promise<void>((done) => {
                    clearInterval(sweep);
                    stopHeartbeat();
                    // Stop every room's timers so nothing fires after the relay is gone.
                    for (const room of rooms.values()) room.shutdown();
                    for (const client of wss.clients) client.terminate();
                    wss.close();
                    server.closeAllConnections();
                    server.close(() => done());
                });

            const shutdown = async (graceMs = 2000) => {
                // Remember who is connected before closeRoom starts closing them.
                const allClosed = Promise.all(
                    [...wss.clients].map((ws) => new Promise<void>((r) => ws.once("close", () => r()))),
                );
                rooms.closeAll("server_shutdown");

                // Wait until every client has closed, or graceMs, whichever comes first.
                let timer: NodeJS.Timeout | undefined;
                const giveUp = new Promise<void>((r) => (timer = setTimeout(r, graceMs)));
                await Promise.race([allClosed, giveUp]);
                clearTimeout(timer);

                await close();
            };

            resolve({ port: bound, rooms, close, shutdown });
        });
    });
}
