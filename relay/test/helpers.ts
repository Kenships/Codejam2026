import type { Conn } from "../src/room.ts";
import type { ServerMessage } from "../src/protocol.ts";

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
