/**
 * Timers and shutdown: grace periods, idle rooms, heartbeat, SIGTERM-style
 * shutdown. Each test starts its own relay with short timings (milliseconds
 * instead of 30-60 s) so the whole file runs in about a second.
 */
import { afterEach, describe, test } from "node:test";
import assert from "node:assert/strict";
import { startRelay, type Relay } from "../src/server.ts";
import type { Timings } from "../src/config.ts";
import { connect, setupRoom } from "./helpers.ts";

/** Long enough that a timer a test isn't about never fires during it. */
const NEVER = 60_000;

let relay: Relay | undefined;

/** Start this test's relay. Anything not overridden effectively never fires. */
async function start(timings: Partial<Timings>) {
    relay = await startRelay({
        port: 0,
        host: "127.0.0.1",
        timings: { playerGraceMs: NEVER, hostGraceMs: NEVER, idleRoomMs: NEVER, sweepMs: NEVER, heartbeatMs: NEVER, ...timings },
    });
    return relay;
}

afterEach(async () => {
    await relay?.close();
    relay = undefined;
});

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

describe("player grace period", () => {
    test("a player who doesn't come back is removed: player_left {timeout}, name free again", async () => {
        const { port } = await start({ playerGraceMs: 150 });
        const { host, room, players } = await setupRoom(port, ["Harry"]);
        const a = players[0]!;

        a.client.close();
        await host.expect("player_disconnected");
        assert.deepEqual(await host.expect("player_left"), { t: "player_left", playerId: a.playerId, reason: "timeout" });

        // The seat is gone, so the same name can join as a brand new player.
        const again = await connect(port);
        again.send({ t: "join", room, name: "Harry" });
        assert.equal((await again.expect("joined")).rejoined, false);
    });

    test("rejoining in time cancels the removal", async () => {
        const { port } = await start({ playerGraceMs: 150 });
        const { host, room, players } = await setupRoom(port, ["Harry"]);
        const a = players[0]!;

        a.client.close();
        await host.expect("player_disconnected");
        const back = await connect(port);
        back.send({ t: "join", room, name: "Harry", playerId: a.playerId });
        await back.expect("joined");
        await host.expect("player_rejoined");

        await host.expectSilence(300); // well past the 150 ms grace: no player_left
    });

    test("drop, rejoin, drop again: removed a FULL grace after the second drop", async () => {
        // The bug this guards against: the first drop's timer is never cancelled,
        // fires at 400 ms, and removes the player only ~200 ms after drop #2.
        const { port } = await start({ playerGraceMs: 400 });
        const { host, room, players } = await setupRoom(port, ["Harry"]);
        const a = players[0]!;

        a.client.close(); // drop #1 (t = 0)
        await host.expect("player_disconnected");
        await sleep(200);

        const tab = await connect(port);
        tab.send({ t: "join", room, name: "Harry", playerId: a.playerId });
        await tab.expect("joined");
        await host.expect("player_rejoined");

        tab.close(); // drop #2 (t ≈ 200 ms)
        await host.expect("player_disconnected");

        // t ≈ 450 ms: drop #1's timer would have fired by now if it were still running.
        await host.expectSilence(250);
        // Drop #2's timer fires at t ≈ 600 ms.
        assert.equal((await host.expect("player_left")).reason, "timeout");
    });
});

describe("host grace period", () => {
    test("a host who doesn't come back: players get host_lost then room_closed {host_timeout}", async () => {
        const { port } = await start({ hostGraceMs: 150 });
        const { host, room, hostKey, players } = await setupRoom(port, ["A", "B"]);

        host.close();
        for (const p of players) await p.client.expect("host_lost");
        for (const p of players) {
            assert.deepEqual(await p.client.expect("room_closed"), { t: "room_closed", reason: "host_timeout" });
            assert.equal(await p.client.closed, 1000);
        }

        // The room is gone for good.
        const late = await connect(port);
        late.send({ t: "host_resume", room, hostKey });
        assert.equal((await late.expect("error")).code, "room_not_found");
    });

    test("resuming in time cancels the room closing", async () => {
        const { port } = await start({ hostGraceMs: 150 });
        const { host, room, hostKey, players } = await setupRoom(port, ["A"]);
        const a = players[0]!;

        host.close();
        await a.client.expect("host_lost");
        const host2 = await connect(port);
        host2.send({ t: "host_resume", room, hostKey });
        await host2.expect("host_resumed");
        await a.client.expect("host_back");

        await a.client.expectSilence(300); // well past the 150 ms grace: no room_closed
    });
});

describe("idle rooms", () => {
    test("a silent room is closed: everyone gets room_closed {idle}", async () => {
        const { port } = await start({ idleRoomMs: 150, sweepMs: 30 });
        const { host, players } = await setupRoom(port, ["A"]);

        for (const c of [host, players[0]!.client]) {
            assert.deepEqual(await c.expect("room_closed"), { t: "room_closed", reason: "idle" });
            assert.equal(await c.closed, 1000);
        }
    });

    test("a room that keeps talking stays open", async () => {
        const { port } = await start({ idleRoomMs: 150, sweepMs: 30 });
        const { host, players } = await setupRoom(port, ["A"]);
        const a = players[0]!;

        // A message every 50 ms for 500 ms: the room is never silent for 150 ms.
        for (let i = 0; i < 10; i++) {
            a.client.send({ t: "send", to: "host", d: { t: "ping" } });
            await host.expect("msg");
            await sleep(50);
        }
        await host.expectSilence(50); // no room_closed arrived
    });
});

describe("heartbeat", () => {
    test("a client that stops answering pings is terminated and counts as a drop", async () => {
        const { port } = await start({ heartbeatMs: 50 });
        const { host, room } = await setupRoom(port, []);

        // autoPong: false = the client never answers pings, like a phone that lost signal.
        const ghost = await connect(port, { autoPong: false });
        ghost.send({ t: "join", room, name: "Ghost" });
        const { playerId } = await ghost.expect("joined");
        await host.expect("player_joined");

        assert.deepEqual(await host.expect("player_disconnected"), { t: "player_disconnected", playerId });
        assert.equal(await ghost.closed, 1006); // cut off, no closing handshake
    });

    test("healthy clients answer pings and stay connected", async () => {
        const { port } = await start({ heartbeatMs: 50 });
        const { host, players } = await setupRoom(port, ["A"]);
        await sleep(300); // 6 heartbeat ticks
        await host.expectSilence(0);
        players[0]!.client.send({ t: "send", to: "host", d: { t: "still here" } });
        assert.equal((await host.expect("msg")).from, players[0]!.playerId);
    });
});

describe("shutdown", () => {
    test("everyone gets room_closed {server_shutdown}, then close code 1001", async () => {
        const r = await start({});
        const one = await setupRoom(r.port, ["A"]);
        const two = await setupRoom(r.port, ["B"]);

        await r.shutdown();
        relay = undefined; // already closed; afterEach has nothing to do

        for (const c of [one.host, one.players[0]!.client, two.host, two.players[0]!.client]) {
            assert.deepEqual(await c.expect("room_closed"), { t: "room_closed", reason: "server_shutdown" });
            assert.equal(await c.closed, 1001);
        }
    });
});
