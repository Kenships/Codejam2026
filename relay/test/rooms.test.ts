import { describe, test } from "node:test";
import assert from "node:assert/strict";
import { Rooms } from "../src/rooms.ts";
import { fakeConn } from "./helpers.ts";

describe("Rooms", () => {
    test("create registers a room with a unique code and the given host", () => {
        const rooms = new Rooms();
        const host = fakeConn("host");
        const room = rooms.create(host);
        assert.equal(rooms.size, 1);
        assert.equal(rooms.get(room.getCode()), room);
        assert.equal(room.getHost(), host);
    });

    test("lookup is case-insensitive and ignores surrounding spaces", () => {
        const rooms = new Rooms();
        const room = rooms.create(fakeConn());
        assert.equal(rooms.get(` ${room.getCode().toLowerCase()} `), room);
    });

    test("unknown code returns undefined", () => {
        assert.equal(new Rooms().get("ZZZZ"), undefined);
    });

    test("many rooms all get different codes", () => {
        const rooms = new Rooms();
        const codes = new Set(Array.from({ length: 200 }, () => rooms.create(fakeConn()).getCode()));
        assert.equal(codes.size, 200);
        assert.equal(rooms.size, 200);
    });

    test("delete removes the room", () => {
        const rooms = new Rooms();
        const code = rooms.create(fakeConn()).getCode();
        assert.equal(rooms.delete(code.toLowerCase()), true);
        assert.equal(rooms.get(code), undefined);
        assert.equal(rooms.delete(code), false);
    });
});
