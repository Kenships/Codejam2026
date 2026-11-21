import { describe, test } from "node:test";
import assert from "node:assert/strict";
import { Room } from "../src/room.ts";
import { resolveTargets, type RouteResult, type Sender } from "../src/routing.ts";
import { fakeConn, type FakeConn } from "./helpers.ts";

/**
 * A room with a host and players A, B, C. Returns the senders and conns
 * so each test can route from whoever it likes.
 */
function setup() {
    const hostConn = fakeConn("host");
    const room = new Room("KXQM", "key", hostConn);

    const conns: Record<string, FakeConn> = {};
    const ids: Record<string, string> = {};
    for (const name of ["A", "B", "C"]) {
        const conn = fakeConn(name);
        const r = room.addPlayer(name, conn);
        assert.ok(r.ok);
        conns[name] = conn;
        ids[name] = r.seat.playerId;
    }

    const host: Sender = { kind: "host", room };
    const player = (name: string): Sender => ({ kind: "player", room, playerId: ids[name]! });
    const drop = (name: string) => room.markDisconnected(ids[name]!, conns[name]!);

    return { room, hostConn, ids, host, player, drop };
}

/**
 * Turn a result into something easy to compare:
 * the sorted labels of the target conns, or the error code.
 */
function targets(result: RouteResult): string[] | string {
    if (!result.ok) return result.code;
    return result.conns.map((c) => (c as FakeConn).label).sort();
}

describe("resolveTargets: to \"host\"", () => {
    test("(a) host -> host is forbidden", () => {
        const { room, host } = setup();
        assert.equal(targets(resolveTargets(room, host, "host")), "forbidden");
    });

    test("(b) player -> host reaches the host", () => {
        const { room, player } = setup();
        assert.deepEqual(targets(resolveTargets(room, player("A"), "host")), ["host"]);
    });

    test("(b) player -> host while the host is disconnected: dropped, not an error", () => {
        const { room, player } = setup();
        room.setHost(null);
        assert.deepEqual(targets(resolveTargets(room, player("A"), "host")), []);
    });
});

describe("resolveTargets: to \"all\" / \"players\"", () => {
    test("(c) player without the grant -> forbidden, for both", () => {
        const { room, player } = setup();
        assert.equal(targets(resolveTargets(room, player("A"), "all")), "forbidden");
        assert.equal(targets(resolveTargets(room, player("A"), "players")), "forbidden");
    });

    test("(c) a grant for someone else doesn't help", () => {
        const { room, ids, player } = setup();
        room.setBroadcasterId(ids["B"]!);
        assert.equal(targets(resolveTargets(room, player("A"), "all")), "forbidden");
    });

    test("(d) broadcaster -> all: host + other players, never the sender", () => {
        const { room, ids, player } = setup();
        room.setBroadcasterId(ids["A"]!);
        assert.deepEqual(targets(resolveTargets(room, player("A"), "all")), ["B", "C", "host"]);
    });

    test("(d) broadcaster -> players: other players only, no host, not the sender", () => {
        const { room, ids, player } = setup();
        room.setBroadcasterId(ids["A"]!);
        assert.deepEqual(targets(resolveTargets(room, player("A"), "players")), ["B", "C"]);
    });

    test("(d) broadcaster -> all while the host is disconnected: just the other players", () => {
        const { room, ids, player } = setup();
        room.setBroadcasterId(ids["A"]!);
        room.setHost(null);
        assert.deepEqual(targets(resolveTargets(room, player("A"), "all")), ["B", "C"]);
    });

    test("(d) disconnected players are skipped", () => {
        const { room, ids, player, drop } = setup();
        room.setBroadcasterId(ids["A"]!);
        drop("B");
        assert.deepEqual(targets(resolveTargets(room, player("A"), "all")), ["C", "host"]);
    });

    test("(d) host -> all and host -> players: every connected player, host not included", () => {
        const { room, host } = setup();
        assert.deepEqual(targets(resolveTargets(room, host, "all")), ["A", "B", "C"]);
        assert.deepEqual(targets(resolveTargets(room, host, "players")), ["A", "B", "C"]);
    });

    test("(d) host -> all skips disconnected players", () => {
        const { room, host, drop } = setup();
        drop("C");
        assert.deepEqual(targets(resolveTargets(room, host, "all")), ["A", "B"]);
    });
});

describe("resolveTargets: to a playerId", () => {
    test("(e) player -> another player is forbidden", () => {
        const { room, ids, player } = setup();
        assert.equal(targets(resolveTargets(room, player("A"), ids["B"]!)), "forbidden");
    });

    test("(e) still forbidden even for the broadcaster", () => {
        const { room, ids, player } = setup();
        room.setBroadcasterId(ids["A"]!);
        assert.equal(targets(resolveTargets(room, player("A"), ids["B"]!)), "forbidden");
    });

    test("(f) host -> unknown playerId is unknown_target", () => {
        const { room, host } = setup();
        assert.equal(targets(resolveTargets(room, host, "nobody")), "unknown_target");
    });

    test("(g) host -> connected player reaches only that player", () => {
        const { room, ids, host } = setup();
        assert.deepEqual(targets(resolveTargets(room, host, ids["B"]!)), ["B"]);
    });

    test("(g) host -> disconnected player: dropped, not an error", () => {
        const { room, ids, host, drop } = setup();
        drop("B");
        assert.deepEqual(targets(resolveTargets(room, host, ids["B"]!)), []);
    });
});
