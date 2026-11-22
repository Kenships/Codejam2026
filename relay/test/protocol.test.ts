import { describe, test } from "node:test";
import assert from "node:assert/strict";
import { parseClientMessage } from "../src/protocol.ts";

/** Parse something that must succeed and hand back the typed message. */
function ok(raw: string) {
    const r = parseClientMessage(raw);
    assert.ok(r.ok, `expected ok, got ${JSON.stringify(r)}`);
    return r.msg;
}

/** Parse something that must fail and hand back the error code. */
function fails(raw: string) {
    const r = parseClientMessage(raw);
    assert.ok(!r.ok, `expected failure, got ${JSON.stringify(r)}`);
    return r.code;
}

describe("parseClientMessage: not a JSON object", () => {
    test("invalid JSON", () => assert.equal(fails("not json"), "bad_json"));
    test("empty string", () => assert.equal(fails(""), "bad_json"));
    test("array", () => assert.equal(fails("[1,2]"), "bad_json"));
    test("null", () => assert.equal(fails("null"), "bad_json"));
    test("string", () => assert.equal(fails('"hi"'), "bad_json"));
    test("number", () => assert.equal(fails("42"), "bad_json"));
});

describe("parseClientMessage: message types", () => {
    test("unknown t", () => assert.equal(fails('{"t":"hack"}'), "bad_message"));
    test("missing t", () => assert.equal(fails('{"room":"KXQM"}'), "bad_message"));

    test("host_create", () => {
        assert.deepEqual(ok('{"t":"host_create"}'), { t: "host_create" });
    });

    test("host_resume needs room and hostKey", () => {
        assert.deepEqual(ok('{"t":"host_resume","room":"KXQM","hostKey":"k"}'), {
            t: "host_resume", room: "KXQM", hostKey: "k",
        });
        assert.equal(fails('{"t":"host_resume","room":"KXQM"}'), "bad_message");
    });

    test("join without playerId", () => {
        const msg = ok('{"t":"join","room":"kxqm","name":"Harry"}');
        assert.equal(msg.t, "join");
        if (msg.t === "join") {
            assert.equal(msg.room, "kxqm");
            assert.equal(msg.name, "Harry");
            assert.equal(msg.playerId, undefined);
        }
    });

    test("join with playerId", () => {
        const msg = ok('{"t":"join","room":"KXQM","name":"Harry","playerId":"p1"}');
        assert.equal(msg.t === "join" && msg.playerId, "p1");
    });

    test("join rejects wrong field types", () => {
        assert.equal(fails('{"t":"join","room":5,"name":"Harry"}'), "bad_message");
        assert.equal(fails('{"t":"join","room":"KXQM"}'), "bad_message");
        assert.equal(fails('{"t":"join","room":"KXQM","name":"H","playerId":7}'), "bad_message");
    });

    test("send", () => {
        assert.deepEqual(ok('{"t":"send","to":"all","d":{"t":"guess","hex":"#FF0000"}}'), {
            t: "send", to: "all", d: { t: "guess", hex: "#FF0000" },
        });
    });

    test("send rejects empty to, and non-object d", () => {
        assert.equal(fails('{"t":"send","to":"","d":{}}'), "bad_message");
        assert.equal(fails('{"t":"send","to":"host","d":[1]}'), "bad_message");
        assert.equal(fails('{"t":"send","to":"host","d":null}'), "bad_message");
        assert.equal(fails('{"t":"send","to":"host","d":"hi"}'), "bad_message");
        assert.equal(fails('{"t":"send","to":"host"}'), "bad_message");
    });

    test("send.retain: true is kept, false/absent is dropped, non-boolean rejected", () => {
        assert.deepEqual(ok('{"t":"send","to":"players","d":{"t":"state"},"retain":true}'), {
            t: "send", to: "players", d: { t: "state" }, retain: true,
        });
        assert.equal("retain" in ok('{"t":"send","to":"players","d":{},"retain":false}'), false);
        assert.equal(fails('{"t":"send","to":"players","d":{},"retain":"yes"}'), "bad_message");
    });

    test("grant_broadcast accepts a string or explicit null only", () => {
        assert.deepEqual(ok('{"t":"grant_broadcast","playerId":"p1"}'), { t: "grant_broadcast", playerId: "p1" });
        assert.deepEqual(ok('{"t":"grant_broadcast","playerId":null}'), { t: "grant_broadcast", playerId: null });
        assert.equal(fails('{"t":"grant_broadcast"}'), "bad_message");
    });

    test("kick", () => {
        assert.deepEqual(ok('{"t":"kick","playerId":"p1"}'), { t: "kick", playerId: "p1" });
        assert.equal(fails('{"t":"kick"}'), "bad_message");
    });
});

describe("parseClientMessage: security", () => {
    test("a client-supplied `from` is stripped", () => {
        const msg = ok('{"t":"send","to":"host","d":{"t":"guess"},"from":"host"}');
        assert.equal("from" in msg, false);
    });

    test("unknown extra fields are stripped", () => {
        const msg = ok('{"t":"kick","playerId":"p1","admin":true}');
        assert.deepEqual(Object.keys(msg).sort(), ["playerId", "t"]);
    });
});
