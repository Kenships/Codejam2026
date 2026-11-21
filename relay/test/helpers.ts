import assert from "node:assert/strict";
import WebSocket, { type ClientOptions } from "ws";
import type { Conn } from "../src/room.ts";
import type { ServerMessage } from "../src/protocol.ts";
import { setLogLevel } from "../src/log.ts";

// Keep test output clean. Comment out (or use "debug") to see relay logs while debugging a test.
setLogLevel("silent");

// ---------------------------------------------------------------------------
// Fake connections (unit tests: no networking)
// ---------------------------------------------------------------------------

/**
 * A pretend connection for tests. Instead of touching a real socket it
 * records everything sent to it and every close() call, so a test can
 * check "did the host receive player_joined?" without any networking.
 */
export type FakeConn = Conn & {
    label: string;
    sent: ServerMessage[];
    closed: { code: number; reason?: string }[];
};

export function fakeConn(label = "conn"): FakeConn {
    const conn: FakeConn = {
        label,
        sent: [],
        closed: [],
        send(msg) {
            conn.sent.push(msg);
        },
        close(code, reason) {
            conn.closed.push({ code, reason });
        },
    };
    return conn;
}

// ---------------------------------------------------------------------------
// Real WebSocket clients (integration tests against a running relay)
// ---------------------------------------------------------------------------

type ServerMessageOf<T extends ServerMessage["t"]> = Extract<ServerMessage, { t: T }>;

export type TestClient = {
    /** Send an object as a JSON text frame. */
    send(msg: unknown): void;
    /** Send exactly these bytes/text (for malformed-input tests). */
    sendRaw(data: string | Buffer): void;
    /** Wait for the next message from the relay, in order. */
    next(timeoutMs?: number): Promise<ServerMessage>;
    /** Wait for the next message and assert its type; returns it already narrowed. */
    expect<T extends ServerMessage["t"]>(t: T, timeoutMs?: number): Promise<ServerMessageOf<T>>;
    /** Assert nothing (more) arrives within `ms`. */
    expectSilence(ms?: number): Promise<void>;
    /** Resolves with the close code once the socket closes. */
    closed: Promise<number>;
    close(): void;
};

/**
 * Open a real WebSocket to the relay and wrap it in a test-friendly API.
 * Pass { autoPong: false } to imitate a client that died silently.
 */
export function connect(port: number, options?: ClientOptions): Promise<TestClient> {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`, options);
    const queue: ServerMessage[] = [];
    const waiters: ((msg: ServerMessage) => void)[] = [];

    ws.on("message", (data) => {
        const msg = JSON.parse(data.toString()) as ServerMessage;
        const waiter = waiters.shift();
        if (waiter) waiter(msg);
        else queue.push(msg);
    });

    const closed = new Promise<number>((resolve) => ws.on("close", (code) => resolve(code)));

    const client: TestClient = {
        send: (msg) => ws.send(JSON.stringify(msg)),
        sendRaw: (data) => ws.send(data),

        next(timeoutMs = 1000) {
            const queued = queue.shift();
            if (queued) return Promise.resolve(queued);
            return new Promise((resolve, reject) => {
                const waiter = (msg: ServerMessage) => {
                    clearTimeout(timer);
                    resolve(msg);
                };
                const timer = setTimeout(() => {
                    waiters.splice(waiters.indexOf(waiter), 1);
                    reject(new Error(`timed out after ${timeoutMs}ms waiting for a message`));
                }, timeoutMs);
                waiters.push(waiter);
            });
        },

        async expect(t, timeoutMs) {
            const msg = await client.next(timeoutMs);
            assert.equal(msg.t, t, `expected "${t}", got ${JSON.stringify(msg)}`);
            return msg as ServerMessageOf<typeof t>;
        },

        async expectSilence(ms = 100) {
            await new Promise((r) => setTimeout(r, ms));
            assert.equal(queue.length, 0, `expected no messages, got ${JSON.stringify(queue)}`);
        },

        closed,
        close: () => ws.close(),
    };

    return new Promise((resolve, reject) => {
        ws.once("open", () => resolve(client));
        ws.once("error", reject);
    });
}

// ---------------------------------------------------------------------------
// Room setup shared by the integration tests
// ---------------------------------------------------------------------------

export type Player = { client: TestClient; playerId: string; name: string };

/** Host creates a room and each name joins. All setup messages are consumed. */
export async function setupRoom(port: number, names: string[]) {
    const host = await connect(port);
    host.send({ t: "host_create" });
    const { room, hostKey } = await host.expect("room_created");

    const players: Player[] = [];
    for (const name of names) {
        const client = await connect(port);
        client.send({ t: "join", room, name });
        const joined = await client.expect("joined");
        await host.expect("player_joined");
        players.push({ client, playerId: joined.playerId, name });
    }
    return { host, room, hostKey, players };
}
