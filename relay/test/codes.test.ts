import { describe, test } from "node:test";
import assert from "node:assert/strict";
import { generateHostKey, generatePlayerId, generateRoomCode } from "../src/codes.ts";
import { ROOM_CODE_ALPHABET, ROOM_CODE_LENGTH } from "../src/protocol.ts";

describe("generateRoomCode", () => {
    test("is ROOM_CODE_LENGTH letters from the alphabet (no I, L, O)", () => {
        for (let i = 0; i < 500; i++) {
            const code = generateRoomCode(() => false);
            assert.equal(code.length, ROOM_CODE_LENGTH);
            for (const ch of code) assert.ok(ROOM_CODE_ALPHABET.includes(ch), `bad char ${ch} in ${code}`);
        }
    });

    test("retries while the code is taken, and returns the first free one", () => {
        const seen: string[] = [];
        const code = generateRoomCode((c) => {
            seen.push(c);
            return seen.length <= 3; // first 3 candidates are "taken"
        });
        assert.equal(seen.length, 4);
        assert.equal(code, seen[3]);
        // each attempt is a fresh code, not the previous one with more letters appended
        for (const c of seen) assert.equal(c.length, ROOM_CODE_LENGTH);
    });

    test("throws instead of looping forever when every code is taken", () => {
        assert.throws(() => generateRoomCode(() => true));
    });
});

describe("ids and keys", () => {
    test("player ids are URL-safe and unique", () => {
        const ids = new Set(Array.from({ length: 1000 }, generatePlayerId));
        assert.equal(ids.size, 1000);
        for (const id of ids) assert.match(id, /^[A-Za-z0-9_-]+$/);
    });

    test("host keys are long and unique", () => {
        const a = generateHostKey();
        const b = generateHostKey();
        assert.notEqual(a, b);
        assert.ok(a.length >= 32);
    });
});
