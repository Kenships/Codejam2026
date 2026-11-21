# HueSlider Relay

A small WebSocket server that connects the **Unity host** (the big screen) to the **players' phones**
(the React app). It creates rooms, tracks who is connected, and forwards messages between them.

It does **not** run the game. Phases, timers, colors and scoring all live in Unity. The relay never
looks inside game messages. It only checks who sent a message and who is allowed to receive it.

```
 Phones (React, browser) ──wss──►  Relay  ◄──wss── Unity host (NativeWebSocket)
```

- **Message format:** [PROTOCOL.md](PROTOCOL.md). Read this before writing a client.
- **Stack:** Node.js 22.18+, TypeScript (run directly by Node, no build step), [`ws`](https://github.com/websockets/ws).

## Quick start

```bash
cd relay
npm install
npm run dev          # relay on ws://localhost:8080, restarts when you save a file
```

Check it's up: open <http://localhost:8080/healthz>. It should show `{"ok":true,...}`.

## Try it without Unity or React

Fake command-line clients speak the same protocol as the real ones. Open separate terminals:

```bash
npm run dev                         # 1: the relay
npm run host                        # 2: fake Unity host, prints a ROOM CODE
npm run player -- KXQM Harry        # 3: fake phone (use the printed code)
npm run player -- KXQM Emily        # 4: another phone
```

Then type commands, for example in the host terminal:

```
all {"t":"state","phase":"DRAWING"}
to Harry {"t":"your_target","hex":"#FF5733"}
grant Harry
```

and in Harry's terminal: `all {"t":"stroke_batch","pts":[[0.1,0.2]]}`.
Type `help` in any terminal for the full list (drop, rejoin, kick, second tab, host resume...).
A single word is shorthand for a message type: `all state` sends `{"t":"state"}`.

To use a relay that isn't on your machine: `npm run host -- wss://<relay-url>`, or set `RELAY_URL`.

## Scripts

| Command | What it does |
|---|---|
| `npm run dev` | Run the relay, reload on save, read settings from `.env` |
| `npm start` | Run the relay as production does (no reload, no `.env` file) |
| `npm test` | Run all tests (~130, about 3 seconds) |
| `npm run typecheck` | Check types. **Node doesn't check types when it runs**, so run this before committing. |
| `npm run host` / `npm run player` | Fake clients (see above) |

## Configuration

Settings come from environment variables. Locally, put them in `relay/.env` (copy `.env.example`).
In production, set them on the hosting platform.

| Variable | Default | Meaning |
|---|---|---|
| `PORT` | `8080` | Port to listen on. Hosting platforms set this for you. |
| `ALLOWED_ORIGINS` | *(empty)* | Comma-separated websites allowed to connect from a browser, e.g. `https://hueslider.pages.dev,https://*.hueslider.pages.dev`. **Empty allows any site** (fine locally, set it in production). Unity sends no origin and is always allowed. |
| `LOG_LEVEL` | `info` | `debug`, `info`, `warn`, `error` or `silent`. `debug` adds every routed message (its type, never its contents). |

Timeouts and limits (grace periods, idle timeout, heartbeat, rate limit) are in
[src/config.ts](src/config.ts) and described in PROTOCOL.md sections 1 and 5.

## Reading the logs

One line per event, with `conn=N` numbering each socket so you can follow one phone:

```
INFO  room created conn=1 room=EBHZ role=host
INFO  player joined conn=2 room=EBHZ player=4xWNPO4 name=Harry
INFO  player disconnected conn=2 room=EBHZ player=4xWNPO4 code=1006
INFO  player rejoined conn=3 room=EBHZ player=4xWNPO4 name=Harry replacedOpenSocket=false
WARN  origin rejected origin=https://evil.example
```

Common close codes: `1000` normal, `1001` relay restarting, `1006` connection lost,
`1008` origin not allowed, `1009` message too big, `4000` kicked, `4002` replaced by a second tab.

## How it works

| File | Job |
|---|---|
| [src/index.ts](src/index.ts) | Entry point: start the relay, shut down cleanly on SIGTERM / Ctrl+C |
| [src/server.ts](src/server.ts) | HTTP server (`/healthz`), WebSocket upgrade, origin check, idle sweep |
| [src/connection.ts](src/connection.ts) | Per-socket logic: parse each message, run its handler, handle disconnects |
| [src/protocol.ts](src/protocol.ts) | Message types, error codes, and the parser for untrusted input |
| [src/routing.ts](src/routing.ts) | Who may send to whom (`resolveTargets`) |
| [src/room.ts](src/room.ts) / [src/rooms.ts](src/rooms.ts) | Room and seat bookkeeping, grace timers, closing rooms |
| [src/heartbeat.ts](src/heartbeat.ts) | Pings every socket. Terminates ones that stop answering. |
| [src/rateLimit.ts](src/rateLimit.ts) | Per-socket token bucket |
| [src/origin.ts](src/origin.ts) | `ALLOWED_ORIGINS` matching |
| [src/log.ts](src/log.ts) | Leveled `key=value` logging |
| [scripts/](scripts/) | The fake host / player CLIs |
| [test/](test/) | Unit tests plus real-socket tests (each starts its own relay on a random port) |

Main guarantees, all covered by tests:

- **`from` is always set by the relay.** Clients can't impersonate the host or another player.
- **The secret target color is safe:** players can't message each other directly. Only the player
  the host grants broadcast permission (the artist) can send to everyone.
- **Reconnects keep your seat:** a dropped phone has 30 s to rejoin with its `playerId`. A dropped
  host has 60 s to resume with its `hostKey`. A second tab replaces the first.
- **One bad client can't take the relay down:** messages over 64 KB, malformed JSON, floods
  (rate limit) and crashing handlers are all contained to that one socket.

## Testing a change

```bash
npm run typecheck && npm test
```

Run one file, or one test by name:

```bash
node --test test/connection.test.ts
node --test --test-name-pattern="second tab" test/connection.test.ts
```

To see relay logs while a test runs, change `setLogLevel("silent")` to `"debug"` in
[test/helpers.ts](test/helpers.ts).

## Deploying

*To be written once the deployment plan is decided.*
