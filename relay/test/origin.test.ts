import { describe, test } from "node:test";
import assert from "node:assert/strict";
import { isOriginAllowed } from "../src/origin.ts";

const allowed = ["http://localhost:5173", "https://hueslider.pages.dev", "https://*.hueslider.pages.dev"];

describe("isOriginAllowed", () => {
    test("no Origin header (Unity, CLI) is always allowed", () => {
        assert.equal(isOriginAllowed(undefined, allowed), true);
    });

    test("an empty allow-list allows everything (local dev)", () => {
        assert.equal(isOriginAllowed("https://evil.example", []), true);
    });

    test("exact matches", () => {
        assert.equal(isOriginAllowed("http://localhost:5173", allowed), true);
        assert.equal(isOriginAllowed("https://hueslider.pages.dev", allowed), true);
    });

    test("anything else is rejected", () => {
        assert.equal(isOriginAllowed("https://evil.example", allowed), false);
        assert.equal(isOriginAllowed("http://localhost:3000", allowed), false, "different port");
        assert.equal(isOriginAllowed("https://localhost:5173", allowed), false, "different scheme");
        assert.equal(isOriginAllowed("http://hueslider.pages.dev", allowed), false, "http instead of https");
    });

    test("wildcard matches Pages preview subdomains", () => {
        assert.equal(isOriginAllowed("https://a1b2c3.hueslider.pages.dev", allowed), true);
        assert.equal(isOriginAllowed("https://server.hueslider.pages.dev", allowed), true);
    });

    test("wildcard is not fooled by look-alike domains", () => {
        assert.equal(isOriginAllowed("https://evilhueslider.pages.dev", allowed), false, "no dot before the domain");
        assert.equal(isOriginAllowed("https://hueslider.pages.dev.evil.example", allowed), false, "domain as a prefix");
        assert.equal(isOriginAllowed("https://.hueslider.pages.dev", allowed), false, "empty subdomain");
        assert.equal(isOriginAllowed("http://a1b2c3.hueslider.pages.dev", allowed), false, "wrong scheme");
    });

    test("case and a trailing slash in the config don't matter", () => {
        assert.equal(isOriginAllowed("HTTPS://HueSlider.Pages.Dev", allowed), true);
        assert.equal(isOriginAllowed("https://hueslider.pages.dev", ["https://hueslider.pages.dev/"]), true);
    });
});
