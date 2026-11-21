/**
 * End-to-end tests: a real relay on a random port, real WebSocket clients.
 * Each test starts a fresh relay, so tests can't affect each other.
 */
import { afterEach, beforeEach, describe, test } from "node:test";
import assert from "node:assert/strict";
import { startRelay, type Relay } from "../src/server.ts";
import { connect, setupRoom as setupRoomAt, type Player } from "./helpers.ts";

let relay: Relay;
beforeEach(async () => {
    // Bind to 127.0.0.1 itself: with port 0 on all interfaces, the OS can hand
    // out a port another local app already holds on 127.0.0.1 (seen as a 404).
    relay = await startRelay({ port: 0, host: "127.0.0.1" });
});
afterEach(async () => {
    await relay.close();
});

/** Host creates a room on this test's relay and each name joins. */
const setupRoom = (names: string[]) => setupRoomAt(relay.port, names);

// ---------------------------------------------------------------------------

describe("HTTP", () => {
    test("/healthz is 200 JSON", async () => {
        const res = await fetch(`http://127.0.0.1:${relay.port}/healthz`);
        assert.equal(res.status, 200);
        assert.equal(((await res.json()) as { ok: boolean }).ok, true);
    });
});

describe("bad input", () => {
    test("invalid JSON -> error bad_json, and the socket stays usable", async () => {
        const c = await connect(relay.port);
        c.sendRaw("not json");
        assert.equal((await c.expect("error")).code, "bad_json");
        c.send({ t: "host_create" });
        await c.expect("room_created");
    });

    test("unknown type -> error bad_message", async () => {
        const c = await connect(relay.port);
        c.send({ t: "hack" });
        assert.equal((await c.expect("error")).code, "bad_message");
    });

    test("binary frame -> error bad_json (protocol is text frames only)", async () => {
        const c = await connect(relay.port);
        c.sendRaw(Buffer.from('{"t":"host_create"}'));
        assert.equal((await c.expect("error")).code, "bad_json");
    });

    test("frame over 64 KB -> socket closed with 1009", async () => {
        const c = await connect(relay.port);
        c.sendRaw("x".repeat(70 * 1024));
        assert.equal(await c.closed, 1009);
    });
});

describe("identity rules", () => {
    test("send before joining -> not_in_room", async () => {
        const c = await connect(relay.port);
        c.send({ t: "send", to: "host", d: {} });
        assert.equal((await c.expect("error")).code, "not_in_room");
    });

    test("host_create twice -> already_in_room", async () => {
        const { host } = await setupRoom([]);
        host.send({ t: "host_create" });
        assert.equal((await host.expect("error")).code, "already_in_room");
    });

    test("a player can't join again -> already_in_room", async () => {
        const { room, players } = await setupRoom(["A"]);
        players[0]!.client.send({ t: "join", room, name: "B" });
        assert.equal((await players[0]!.client.expect("error")).code, "already_in_room");
    });
});

describe("host_create / join", () => {
    test("room_created has a 4-letter code and a host key", async () => {
        const host = await connect(relay.port);
        host.send({ t: "host_create" });
        const msg = await host.expect("room_created");
        assert.match(msg.room, /^[A-Z]{4}$/);
        assert.ok(msg.hostKey.length >= 32);
    });

    test("join with a lowercase code works; player gets joined, host gets player_joined", async () => {
        const { host, room } = await setupRoom([]);
        const p = await connect(relay.port);
        p.send({ t: "join", room: room.toLowerCase(), name: "Harry" });
        const joined = await p.expect("joined");
        assert.equal(joined.room, room);
        assert.equal(joined.name, "Harry");
        assert.equal(joined.rejoined, false);
        const pj = await host.expect("player_joined");
        assert.deepEqual(pj, { t: "player_joined", playerId: joined.playerId, name: "Harry" });
    });

    test("unknown room -> room_not_found", async () => {
        const p = await connect(relay.port);
        p.send({ t: "join", room: "ZZZZ", name: "Harry" });
        assert.equal((await p.expect("error")).code, "room_not_found");
    });

    test("taken name -> name_taken (and the host hears nothing)", async () => {
        const { host, room } = await setupRoom(["Harry"]);
        const p = await connect(relay.port);
        p.send({ t: "join", room, name: "HARRY" });
        assert.equal((await p.expect("error")).code, "name_taken");
        await host.expectSilence();
    });
});

describe("routing", () => {
    test("player -> host, with `from` stamped by the relay (a fake `from` is ignored)", async () => {
        const { host, players } = await setupRoom(["A"]);
        const a = players[0]!;
        a.client.send({ t: "send", to: "host", from: "host", d: { t: "guess", hex: "#FF5733" } });
        const msg = await host.expect("msg");
        assert.deepEqual(msg, { t: "msg", from: a.playerId, d: { t: "guess", hex: "#FF5733" } });
    });

    test("host -> one player: only that player receives it", async () => {
        const { players, host } = await setupRoom(["A", "B"]);
        const [a, b] = players as [Player, Player];
        host.send({ t: "send", to: a.playerId, d: { t: "your_target", hex: "#00FF00" } });
        assert.deepEqual(await a.client.expect("msg"), { t: "msg", from: "host", d: { t: "your_target", hex: "#00FF00" } });
        await b.client.expectSilence();
    });

    test("host -> all: every player receives it, the host does not", async () => {
        const { host, players } = await setupRoom(["A", "B", "C"]);
        host.send({ t: "send", to: "all", d: { t: "state" } });
        for (const p of players) assert.equal((await p.client.expect("msg")).from, "host");
        await host.expectSilence();
    });

    test("host -> unknown player -> unknown_target", async () => {
        const { host } = await setupRoom(["A"]);
        host.send({ t: "send", to: "nobody", d: {} });
        assert.equal((await host.expect("error")).code, "unknown_target");
    });

    test("host -> \"host\" -> forbidden", async () => {
        const { host } = await setupRoom([]);
        host.send({ t: "send", to: "host", d: {} });
        assert.equal((await host.expect("error")).code, "forbidden");
    });

    test("player -> another player directly -> forbidden", async () => {
        const { players } = await setupRoom(["A", "B"]);
        const [a, b] = players as [Player, Player];
        a.client.send({ t: "send", to: b.playerId, d: {} });
        assert.equal((await a.client.expect("error")).code, "forbidden");
        await b.client.expectSilence();
    });

    test("player -> all without the grant -> forbidden", async () => {
        const { players, host } = await setupRoom(["A", "B"]);
        players[0]!.client.send({ t: "send", to: "all", d: {} });
        assert.equal((await players[0]!.client.expect("error")).code, "forbidden");
        await host.expectSilence();
    });

    test("player with the grant -> all: host and others receive it, sender does not", async () => {
        const { host, players } = await setupRoom(["A", "B", "C"]);
        const [a, b, c] = players as [Player, Player, Player];
        host.send({ t: "grant_broadcast", playerId: a.playerId });
        a.client.send({ t: "send", to: "all", d: { t: "stroke_batch", pts: [[0.1, 0.2]] } });
        for (const r of [host, b.client, c.client]) {
            assert.deepEqual(await r.expect("msg"), { t: "msg", from: a.playerId, d: { t: "stroke_batch", pts: [[0.1, 0.2]] } });
        }
        await a.client.expectSilence();
    });

    test("player with the grant -> players: other players only, not the host", async () => {
        const { host, players } = await setupRoom(["A", "B"]);
        const [a, b] = players as [Player, Player];
        host.send({ t: "grant_broadcast", playerId: a.playerId });
        a.client.send({ t: "send", to: "players", d: {} });
        assert.equal((await b.client.expect("msg")).from, a.playerId);
        await host.expectSilence();
        await a.client.expectSilence();
    });

    test("revoking the grant (null) forbids broadcasting again", async () => {
        const { host, players } = await setupRoom(["A"]);
        const a = players[0]!;
        host.send({ t: "grant_broadcast", playerId: a.playerId });
        host.send({ t: "grant_broadcast", playerId: null });
        a.client.send({ t: "send", to: "all", d: {} });
        assert.equal((await a.client.expect("error")).code, "forbidden");
    });

    test("a message to a disconnected player is dropped silently (no error)", async () => {
        const { host, players } = await setupRoom(["A"]);
        const a = players[0]!;
        a.client.close();
        await host.expect("player_disconnected");
        host.send({ t: "send", to: a.playerId, d: {} });
        await host.expectSilence();
    });
});

describe("host-only actions", () => {
    test("grant_broadcast from a player -> forbidden", async () => {
        const { players } = await setupRoom(["A"]);
        players[0]!.client.send({ t: "grant_broadcast", playerId: players[0]!.playerId });
        assert.equal((await players[0]!.client.expect("error")).code, "forbidden");
    });

    test("grant_broadcast to an unknown player -> unknown_target", async () => {
        const { host } = await setupRoom(["A"]);
        host.send({ t: "grant_broadcast", playerId: "nobody" });
        assert.equal((await host.expect("error")).code, "unknown_target");
    });

    test("kick from a player -> forbidden", async () => {
        const { players } = await setupRoom(["A", "B"]);
        players[0]!.client.send({ t: "kick", playerId: players[1]!.playerId });
        assert.equal((await players[0]!.client.expect("error")).code, "forbidden");
    });

    test("kick: socket closed with 4000, host gets player_left (and NOT player_disconnected)", async () => {
        const { host, players } = await setupRoom(["A"]);
        const a = players[0]!;
        host.send({ t: "kick", playerId: a.playerId });
        assert.equal(await a.client.closed, 4000);
        assert.deepEqual(await host.expect("player_left"), { t: "player_left", playerId: a.playerId, reason: "kicked" });
        await host.expectSilence(150);
    });

    test("kick an unknown player -> unknown_target", async () => {
        const { host } = await setupRoom([]);
        host.send({ t: "kick", playerId: "nobody" });
        assert.equal((await host.expect("error")).code, "unknown_target");
    });
});

describe("player disconnect / rejoin", () => {
    test("drop -> host gets player_disconnected; rejoin -> joined{rejoined:true} + player_rejoined", async () => {
        const { host, room, players } = await setupRoom(["Harry"]);
        const a = players[0]!;
        a.client.close();
        assert.deepEqual(await host.expect("player_disconnected"), { t: "player_disconnected", playerId: a.playerId });

        const again = await connect(relay.port);
        again.send({ t: "join", room, name: "ignored", playerId: a.playerId });
        const joined = await again.expect("joined");
        assert.equal(joined.playerId, a.playerId);
        assert.equal(joined.name, "Harry", "name comes from the original seat");
        assert.equal(joined.rejoined, true);
        assert.deepEqual(await host.expect("player_rejoined"), { t: "player_rejoined", playerId: a.playerId, name: "Harry" });

        // messages now reach the new socket
        host.send({ t: "send", to: a.playerId, d: { t: "snapshot" } });
        assert.equal((await again.expect("msg")).from, "host");
    });

    test("join with an unknown playerId is treated as a new player", async () => {
        const { host, room } = await setupRoom([]);
        const p = await connect(relay.port);
        p.send({ t: "join", room, name: "Harry", playerId: "stale-id" });
        const joined = await p.expect("joined");
        assert.equal(joined.rejoined, false);
        assert.notEqual(joined.playerId, "stale-id");
        await host.expect("player_joined");
    });

    test("second tab: old socket closed with 4002; host gets player_rejoined but NOT player_disconnected", async () => {
        const { host, room, players } = await setupRoom(["Harry"]);
        const a = players[0]!;
        const tab2 = await connect(relay.port);
        tab2.send({ t: "join", room, name: "Harry", playerId: a.playerId });
        assert.equal((await tab2.expect("joined")).rejoined, true);
        assert.equal(await a.client.closed, 4002);
        await host.expect("player_rejoined");
        await host.expectSilence(150);
    });
});

describe("host disconnect / resume", () => {
    test("host drops -> players get host_lost; resume -> host_resumed with players, players get host_back", async () => {
        const { host, room, hostKey, players } = await setupRoom(["A", "B"]);
        host.close();
        for (const p of players) await p.client.expect("host_lost");

        const host2 = await connect(relay.port);
        host2.send({ t: "host_resume", room, hostKey });
        const resumed = await host2.expect("host_resumed");
        assert.equal(resumed.room, room);
        assert.deepEqual(
            resumed.players.map((p) => p.name),
            ["A", "B"],
        );
        assert.ok(resumed.players.every((p) => p.connected));
        for (const p of players) await p.client.expect("host_back");

        // the resumed host receives player messages again
        players[0]!.client.send({ t: "send", to: "host", d: { t: "guess" } });
        assert.equal((await host2.expect("msg")).from, players[0]!.playerId);
    });

    test("host_resume with the wrong key -> bad_host_key", async () => {
        const { host, room } = await setupRoom([]);
        host.close();
        const h2 = await connect(relay.port);
        h2.send({ t: "host_resume", room, hostKey: "wrong" });
        assert.equal((await h2.expect("error")).code, "bad_host_key");
    });

    test("host_resume while the host is still connected -> host_active", async () => {
        const { room, hostKey } = await setupRoom([]);
        const h2 = await connect(relay.port);
        h2.send({ t: "host_resume", room, hostKey });
        assert.equal((await h2.expect("error")).code, "host_active");
    });

    test("host_resume for an unknown room -> room_not_found", async () => {
        const h = await connect(relay.port);
        h.send({ t: "host_resume", room: "ZZZZ", hostKey: "x" });
        assert.equal((await h.expect("error")).code, "room_not_found");
    });
});
