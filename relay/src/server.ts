import http from "node:http";
import type { AddressInfo } from "node:net";
import { WebSocketServer } from "ws";
import { config } from "./config.ts";
import { Rooms } from "./rooms.ts";
import { handleConnection } from "./connection.ts";

export type Relay = {
    /** The port actually bound (useful when you asked for port 0). */
    port: number;
    rooms: Rooms;
    /** Disconnect every client and stop listening. */
    close(): Promise<void>;
};

/**
 * Build the HTTP + WebSocket server and start listening.
 * Pass port 0 to let the OS pick a free port (used by tests).
 */
export function startRelay(port: number): Promise<Relay> {
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
        // TODO step 6: origin check
        wss.handleUpgrade(req, socket, head, (ws) => wss.emit("connection", ws, req));
    });

    wss.on("connection", (ws) => handleConnection(ws, rooms));

    return new Promise((resolve, reject) => {
        server.once("error", reject);
        server.listen(port, () => {
            const bound = (server.address() as AddressInfo).port;
            resolve({
                port: bound,
                rooms,
                close: () =>
                    new Promise<void>((done) => {
                        for (const client of wss.clients) client.terminate();
                        wss.close();
                        server.closeAllConnections();
                        server.close(() => done());
                    }),
            });
        });
    });
}
