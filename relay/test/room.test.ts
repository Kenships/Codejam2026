import { describe, test } from "node:test";
import assert from "node:assert/strict";
import { Room } from "../src/room.ts";
import { MAX_PLAYERS, NAME_MAX_LENGTH } from "../src/protocol.ts";
import { fakeConn } from "./helpers.ts";

function newRoom() {
    return new Room("KXQM", "secret-key", fakeConn("host"));
}

/** Add a player that must succeed and return its seat. */
function join(room: Room, name: string, conn = fakeConn(name)) {
    const r = room.addPlayer(name, conn);
    assert.ok(r.ok, `expected join to succeed, got ${JSON.stringify(r)}`);
    return r.seat;
}

describe("Room.addPlayer", () => {
    test("adds a seat with a trimmed name and the given conn", () => {
        const room = newRoom();
        const conn = fakeConn("a");
        const seat = join(room, "  Harry  ", conn);
        assert.equal(seat.name, "Harry");
        assert.equal(seat.conn, conn);
        assert.equal(room.getSeat(seat.playerId), seat);
    });

    test("rejects blank and too-long names", () => {
        const room = newRoom();
        assert.deepEqual(room.addPlayer("   ", fakeConn()), { ok: false, code: "name_invalid" });
        assert.deepEqual(room.addPlayer("x".repeat(NAME_MAX_LENGTH + 1), fakeConn()), { ok: false, code: "name_invalid" });
        join(room, "x".repeat(NAME_MAX_LENGTH)); // exactly the limit is fine
    });

    test("rejects duplicate names case-insensitively", () => {
        const room = newRoom();
        join(room, "Harry");
        assert.deepEqual(room.addPlayer(" harry ", fakeConn()), { ok: false, code: "name_taken" });
    });

    test("rejects the 13th player", () => {
        const room = newRoom();
        for (let i = 0; i < MAX_PLAYERS; i++) join(room, `p${i}`);
        assert.deepEqual(room.addPlayer("late", fakeConn()), { ok: false, code: "room_full" });
    });

    test("a disconnected seat still counts toward the limit", () => {
        const room = newRoom();
        const seats = Array.from({ length: MAX_PLAYERS }, (_, i) => join(room, `p${i}`));
        const first = seats[0]!;
        room.markDisconnected(first.playerId, first.conn!);
        assert.deepEqual(room.addPlayer("late", fakeConn()), { ok: false, code: "room_full" });
    });
});

describe("Room.rejoin / markDisconnected", () => {
    test("unknown playerId returns null", () => {
        assert.equal(newRoom().rejoin("nope", fakeConn()), null);
    });

    test("normal reconnect after a drop: oldConn is null, seat gets the new conn", () => {
        const room = newRoom();
        const a = fakeConn("a");
        const seat = join(room, "Harry", a);
        room.markDisconnected(seat.playerId, a);
        assert.equal(seat.conn, null);

        const b = fakeConn("b");
        const r = room.rejoin(seat.playerId, b);
        assert.ok(r);
        assert.equal(r.oldConn, null);
        assert.equal(r.seat.conn, b);
    });

    test("second tab: returns the OLD conn so the caller can close it", () => {
        const room = newRoom();
        const a = fakeConn("tabA");
        const seat = join(room, "Harry", a);
        const b = fakeConn("tabB");
        const r = room.rejoin(seat.playerId, b);
        assert.ok(r);
        assert.equal(r.oldConn, a);
        assert.equal(room.getSeat(seat.playerId)?.conn, b);
    });

    test("a late close from the replaced conn does NOT disconnect the new one", () => {
        const room = newRoom();
        const a = fakeConn("tabA");
        const seat = join(room, "Harry", a);
        const b = fakeConn("tabB");
        room.rejoin(seat.playerId, b);
        room.markDisconnected(seat.playerId, a); // tabA's close event arrives late
        assert.equal(room.getSeat(seat.playerId)?.conn, b);
    });

    test("rejoin keeps the same seat object, name and id", () => {
        const room = newRoom();
        const seat = join(room, "Harry");
        const r = room.rejoin(seat.playerId, fakeConn());
        assert.equal(r?.seat, seat);
        assert.equal(r?.seat.name, "Harry");
    });

    test("markDisconnected returns whether it actually disconnected someone", () => {
        const room = newRoom();
        const a = fakeConn("tabA");
        const seat = join(room, "Harry", a);

        const b = fakeConn("tabB");
        room.rejoin(seat.playerId, b);
        assert.equal(room.markDisconnected(seat.playerId, a), false);
        assert.equal(room.markDisconnected(seat.playerId, b), true);
        assert.equal(room.markDisconnected(seat.playerId, b), false);
        assert.equal(room.markDisconnected("unknown", b), false);
    });
});

describe("Room.removePlayer", () => {
    test("returns the removed seat and frees the slot", () => {
        const room = newRoom();
        const seat = join(room, "Harry");
        assert.equal(room.removePlayer(seat.playerId), seat);
        assert.equal(room.getSeat(seat.playerId), undefined);
        join(room, "Harry"); // name is free again
    });

    test("removing an unknown player returns undefined", () => {
        assert.equal(newRoom().removePlayer("nope"), undefined);
    });
});

describe("Room broadcast grant", () => {
    test("grant to a real player, deny a typo, clear with null", () => {
        const room = newRoom();
        const seat = join(room, "Harry");
        assert.equal(room.setBroadcasterId(seat.playerId), true);
        assert.equal(room.canBroadcast(seat.playerId), true);

        assert.equal(room.setBroadcasterId("nope"), false);
        assert.equal(room.getBroadcasterId(), seat.playerId, "a bad grant must not change the current one");

        assert.equal(room.setBroadcasterId(null), true);
        assert.equal(room.canBroadcast(seat.playerId), false);
    });

    test("only one broadcaster at a time", () => {
        const room = newRoom();
        const a = join(room, "A");
        const b = join(room, "B");
        room.setBroadcasterId(a.playerId);
        room.setBroadcasterId(b.playerId);
        assert.equal(room.canBroadcast(a.playerId), false);
        assert.equal(room.canBroadcast(b.playerId), true);
    });

    test("the grant survives a reconnect (it is tied to playerId, not the conn)", () => {
        const room = newRoom();
        const seat = join(room, "Harry");
        room.setBroadcasterId(seat.playerId);
        room.markDisconnected(seat.playerId, seat.conn!);
        room.rejoin(seat.playerId, fakeConn());
        assert.equal(room.canBroadcast(seat.playerId), true);
    });

    test("removing the broadcaster clears the grant", () => {
        const room = newRoom();
        const seat = join(room, "Harry");
        room.setBroadcasterId(seat.playerId);
        room.removePlayer(seat.playerId);
        assert.equal(room.getBroadcasterId(), null);
    });
});

describe("Room host", () => {
    test("checkHostKey", () => {
        const room = newRoom();
        assert.equal(room.checkHostKey("secret-key"), true);
        assert.equal(room.checkHostKey("wrong"), false);
    });

    test("setHost swaps the host conn and accepts null", () => {
        const room = newRoom();
        room.setHost(null);
        assert.equal(room.getHost(), null);
        const h = fakeConn("hostB");
        room.setHost(h);
        assert.equal(room.getHost(), h);
    });
});

describe("Room.playerList", () => {
    test("reports each seat with connected true/false, in join order", () => {
        const room = newRoom();
        const a = join(room, "A");
        const b = join(room, "B");
        room.markDisconnected(b.playerId, b.conn!);
        assert.deepEqual(room.playerList(), [
            { playerId: a.playerId, name: "A", connected: true },
            { playerId: b.playerId, name: "B", connected: false },
        ]);
    });
});

describe("Room retained messages", () => {
    test("room-wide slot keeps the latest message; {} clears it", () => {
        const room = newRoom();
        assert.equal(room.getRetained(), null);
        room.setRetained({ t: "state", phase: "drawing" });
        room.setRetained({ t: "state", phase: "playing" });
        assert.deepEqual(room.getRetained()?.d, { t: "state", phase: "playing" });
        room.setRetained({});
        assert.equal(room.getRetained(), null);
    });

    test("seat slot is per player and false for an unknown seat", () => {
        const room = newRoom();
        const a = join(room, "A");
        const b = join(room, "B");
        assert.equal(room.setSeatRetained(a.playerId, { t: "snapshot", hex: "#FF0000" }), true);
        assert.deepEqual(room.getSeat(a.playerId)?.retained?.d, { t: "snapshot", hex: "#FF0000" });
        assert.equal(room.getSeat(b.playerId)?.retained, null);
        assert.equal(room.setSeatRetained("p_nobody", { t: "x" }), false);
        room.setSeatRetained(a.playerId, {});
        assert.equal(room.getSeat(a.playerId)?.retained, null);
    });
});
