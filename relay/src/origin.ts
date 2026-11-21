/**
 * Decide whether a WebSocket connection's Origin header is allowed.
 *
 * Browsers always send Origin (the site the page came from), and a page
 * can't fake it. So this stops OTHER websites from connecting to the relay
 * through their visitors' browsers. It is NOT authentication: a script
 * outside a browser can send any Origin it likes.
 *
 * Rules:
 * - No Origin header            → allowed (Unity / NativeWebSocket, CLI tools)
 * - Empty allow-list            → allowed (local dev with ALLOWED_ORIGINS unset)
 * - Exact match                 → allowed, e.g. "https://hueslider.pages.dev"
 * - Wildcard subdomain entry    → "https://*.hueslider.pages.dev" allows
 *                                 Cloudflare Pages preview URLs like
 *                                 "https://a1b2c3.hueslider.pages.dev"
 */
export function isOriginAllowed(origin: string | undefined, allowed: readonly string[]): boolean {
    if (origin === undefined) return true;
    if (allowed.length === 0) return true;

    const actual = origin.toLowerCase();
    return allowed.some((entry) => matches(actual, entry.toLowerCase().replace(/\/+$/, "")));
}

function matches(origin: string, entry: string): boolean {
    const wildcard = "://*.";
    const at = entry.indexOf(wildcard);
    if (at === -1) return origin === entry;

    // "https://*.example.com" → scheme "https", domain "example.com"
    const scheme = entry.slice(0, at);
    const domain = entry.slice(at + wildcard.length);
    const prefix = `${scheme}://`;
    if (!origin.startsWith(prefix) || !origin.endsWith(`.${domain}`)) return false;

    // There must be a real subdomain in front, e.g. not "https://.example.com".
    const sub = origin.slice(prefix.length, origin.length - domain.length - 1);
    return sub.length > 0;
}
