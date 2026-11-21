/**
 * Step 6 limits, end to end: origin check (6a), rate limit (6b).
 */
import { afterEach, describe, test } from "node:test";
import assert from "node:assert/strict";
import { startRelay, type Relay, type RelayOptions } from "../src/server.ts";
import { connect, setupRoom } from "./helpers.ts";

let relay: Relay | undefined;

async function start(options: Omit<RelayOptions, "port" | "host">) {
    relay = await startRelay({ port: 0, host: "127.0.0.1", ...options });
    return relay;
}

afterEach(async () => {
    await relay?.close();
    relay = undefined;
});

describe("origin check", () => {
    const allowedOrigins = ["https://hexcanvas.pages.dev"];

    test("an allowed browser origin connects and works", async () => {
        const { port } = await start({ allowedOrigins });
        const c = await connect(port, { origin: "https://hexcanvas.pages.dev" });
        c.send({ t: "host_create" });
        await c.expect("room_created");
    });

    test("a disallowed origin is closed with 1008", async () => {
        const { port } = await start({ allowedOrigins });
        const c = await connect(port, { origin: "https://evil.example" });
        assert.equal(await c.closed, 1008);
    });

    test("no Origin header (Unity) is allowed", async () => {
        const { port } = await start({ allowedOrigins });
        const c = await connect(port);
        c.send({ t: "host_create" });
        await c.expect("room_created");
    });

    test("an empty allow-list lets any origin in", async () => {
        const { port } = await start({ allowedOrigins: [] });
        const c = await connect(port, { origin: "https://anything.example" });
        c.send({ t: "host_create" });
        await c.expect("room_created");
    });
});

describe("rate limit", () => {
    // Small numbers so the test is quick: a burst of 5, refilling 5 per second.
    const rateLimit = { burst: 5, perSecond: 5 };

    test("a flood gets ONE rate_limited error; excess messages are dropped", async () => {
        const { port } = await start({ rateLimit });
        const { host, players } = await setupRoom(port, ["A"]); // host used 1 token (host_create)
        const a = players[0]!;

        for (let i = 0; i < 20; i++) host.send({ t: "send", to: a.playerId, d: { i } });

        // 4 tokens were left: messages 0-3 get through, the rest are dropped.
        for (let i = 0; i < 4; i++) assert.deepEqual((await a.client.expect("msg")).d, { i });
        await a.client.expectSilence();

        // Exactly one error for the whole flood, not sixteen.
        assert.equal((await host.expect("error")).code, "rate_limited");
        await host.expectSilence();
    });

    test("after slowing down, messages get through again", async () => {
        const { port } = await start({ rateLimit });
        const { host, players } = await setupRoom(port, ["A"]);
        const a = players[0]!;

        for (let i = 0; i < 10; i++) host.send({ t: "send", to: a.playerId, d: { i } });
        for (let i = 0; i < 4; i++) await a.client.expect("msg");
        await host.expect("error");

        await new Promise((r) => setTimeout(r, 400)); // refills 2 tokens
        host.send({ t: "send", to: a.playerId, d: { back: true } });
        assert.deepEqual((await a.client.expect("msg")).d, { back: true });
    });

    test("limits are per socket: one flooding client doesn't block another", async () => {
        const { port } = await start({ rateLimit });
        const { host, players } = await setupRoom(port, ["A"]);
        const a = players[0]!;

        for (let i = 0; i < 20; i++) host.send({ t: "send", to: "all", d: {} });
        await host.expect("error"); // host is limited...

        a.client.send({ t: "send", to: "host", d: { hi: true } }); // ...the player is not
        const msg = await host.expect("msg");
        assert.deepEqual(msg.d, { hi: true });
    });

    test("junk counts too (the check runs before parsing)", async () => {
        const { port } = await start({ rateLimit });
        const c = await connect(port);
        for (let i = 0; i < 10; i++) c.sendRaw("not json");
        for (let i = 0; i < 5; i++) assert.equal((await c.expect("error")).code, "bad_json");
        assert.equal((await c.expect("error")).code, "rate_limited");
        await c.expectSilence();
    });
});
