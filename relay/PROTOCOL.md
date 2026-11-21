# HueSlider Relay Protocol

**Version:** 1
**Audience:** anyone writing a client for the relay (Unity host, React phone app) or changing the relay itself.

The relay is a **dumb message router**. It owns rooms, connections and routing — nothing else.
All game logic (phases, timers, colors, scoring) lives in the **Unity host**. The relay never
inspects the game payload `d`.

```
 Phones (React, browser) ──wss──►  Relay  ◄──wss── Unity host (NativeWebSocket)
```

---

## 1. Transport

| Rule | Value |
|---|---|
| Protocol | Raw WebSocket (RFC 6455). **Not** Socket.IO. |
| URL | `wss://<relay-host>/` in production, `ws://localhost:8080/` locally |
| Frames | UTF-8 **text** frames, each containing exactly one JSON object |
| Max frame size | 64 KB. Larger frames close the socket with code `1009`. |
| Ordering | Messages from one sender arrive in the order they were sent. No ordering is guaranteed *between* different senders. |
| Heartbeat | Relay sends a WebSocket `ping` every 25 s. Browsers and NativeWebSocket answer automatically — clients do nothing. A socket that misses a pong is terminated. |
| Rate limit | Per socket: bursts of up to 40 messages, refilling at 40/second. Over the limit, messages are dropped. The sender gets **one** `error: rate_limited` when it first goes over, then further drops are silent until it slows down. Every frame counts, including invalid ones. |
| Origin | Browser connections must come from an allowed origin (`ALLOWED_ORIGINS`), otherwise the socket is closed with `1008 "origin not allowed"`. Clients that send no `Origin` header (Unity) are always allowed. |
| Room size | 1 host + max **12 players**. Disconnected players still hold a seat (and count) until their grace period ends. |
| HTTP | `GET /healthz` → `200 {"ok":true,"uptime":<seconds>}`. Everything else is `404`. |

Every message, in both directions, has a string field **`t`** (type). Read `t` first, then the
other fields.

---

## 2. Identities

| Thing | Format | Notes |
|---|---|---|
| Room code | 4 uppercase letters from `ABCDEFGHJKMNPQRSTUVWXYZ` (no `I`, `L`, `O`) | Relay uppercases what clients send, so `abcd` works. |
| `playerId` | Opaque string | Issued by the relay on first join. Store it (e.g. `localStorage`) to rejoin. |
| `hostKey` | Opaque secret string | Issued to the host on room creation. Needed to resume a dropped host. **Never send it to phones.** |
| Name | 1–16 characters after trimming whitespace | Must be unique within the room, case-insensitive. |

**One socket = one identity.** A socket becomes a host (via `host_create` / `host_resume`) or a
player (via `join`) exactly once. Any other message before that is rejected with `not_in_room`;
trying to create/join again is rejected with `already_in_room`.

---

## 3. Client → Relay

| `t` | Sender | Fields | Success reply |
|---|---|---|---|
| `host_create` | Host | — | `room_created` |
| `host_resume` | Host | `room: string`, `hostKey: string` | `host_resumed` |
| `join` | Player | `room: string`, `name: string`, `playerId?: string` | `joined` |
| `send` | Anyone in a room | `to: Target`, `d: object` | *(none — forwarded)* |
| `grant_broadcast` | Host | `playerId: string \| null` | *(none)* |
| `kick` | Host | `playerId: string` | *(none — host gets `player_left`)* |

`Target` is one of:

| `to` | Meaning |
|---|---|
| `"host"` | The room's host |
| `"players"` | Every connected player in the room, except the sender |
| `"all"` | Host + every connected player, except the sender |
| `"<playerId>"` | One specific player |

### Routing rules

| Sender | `to: "host"` | `to: "<playerId>"` | `to: "players"` / `"all"` |
|---|---|---|---|
| Host | ✗ `forbidden` | ✅ | ✅ |
| Player | ✅ | ✗ `forbidden` | ✗ `forbidden` |
| Player holding broadcast grant | ✅ | ✗ `forbidden` | ✅ |

- At most **one** player holds the broadcast grant at a time (intended for the current artist, so
  strokes go straight to every screen without a round-trip through Unity).
  `grant_broadcast` with a new `playerId` replaces the previous grant; `null` clears it.
- A message to a player who is currently disconnected is **silently dropped**. The host is
  responsible for re-sending state when it receives `player_rejoined`.
- Sending to an unknown `playerId` → `error: unknown_target`.

### Join / rejoin

- `join` **without** `playerId` → new seat, new `playerId`.
- `join` **with** a `playerId` that holds a seat in that room → rejoin that seat (name is kept from
  the original join; the `name` field is ignored). If that seat still has a live socket, the old
  socket is closed with code `4002` (replaced) — this handles "opened the game in a second tab".
- `join` with an unknown/expired `playerId` → treated as a new join.

---

## 4. Relay → Client

| `t` | Recipient | Fields | When |
|---|---|---|---|
| `room_created` | Host | `room`, `hostKey` | After `host_create` |
| `host_resumed` | Host | `room`, `players: PlayerInfo[]` | After a successful `host_resume` |
| `joined` | Player | `room`, `playerId`, `name`, `rejoined: boolean` | After `join` |
| `msg` | Anyone | `from: "host" \| <playerId>`, `d: object` | A forwarded `send` |
| `player_joined` | Host | `playerId`, `name` | New player took a seat |
| `player_rejoined` | Host | `playerId`, `name` | Player reconnected to their seat |
| `player_disconnected` | Host | `playerId` | Player's socket dropped; seat is held for 30 s |
| `player_left` | Host | `playerId`, `reason: "timeout" \| "kicked"` | Seat released for good |
| `host_lost` | Players | — | Host socket dropped; room held for 60 s |
| `host_back` | Players | — | Host resumed |
| `room_closed` | Everyone | `reason: "host_timeout" \| "idle" \| "server_shutdown"` | Room is gone; socket closes next |
| `error` | Sender | `code: ErrorCode`, `message?: string` | Request rejected. The socket stays open unless noted. |

`PlayerInfo = { playerId: string, name: string, connected: boolean }`

**`from` is always stamped by the relay.** Anything a client puts in its own `from` field is
ignored, so a phone can never impersonate the host or another player.

### Error codes

| `code` | Cause |
|---|---|
| `bad_json` | Frame was not valid JSON, or not a JSON object |
| `bad_message` | Unknown `t`, or required fields missing / wrong type |
| `not_in_room` | `send`/`grant_broadcast`/`kick` before creating or joining |
| `already_in_room` | `host_create`/`host_resume`/`join` on a socket that already has an identity |
| `room_not_found` | No room with that code |
| `room_full` | Room already has 12 players |
| `name_invalid` | Name empty or longer than 16 characters after trimming |
| `name_taken` | Another player in the room has that name |
| `bad_host_key` | `host_resume` with the wrong key |
| `host_active` | `host_resume` while the host is still connected |
| `forbidden` | Routing rule or host-only action violated |
| `unknown_target` | `to` / `playerId` is not a player in this room |
| `rate_limited` | Too many messages |

### WebSocket close codes

| Code | Meaning |
|---|---|
| `1000` | Normal close (e.g. after `room_closed`) |
| `1001` | Relay is shutting down / redeploying |
| `1008` | Policy violation (origin not allowed) |
| `1009` | Frame larger than 64 KB |
| `4000` | Kicked by the host |
| `4002` | Replaced by a newer connection with the same `playerId` |

---

## 5. Timeouts

| Event | Grace period | Then |
|---|---|---|
| Player socket drops | 30 s | `player_left {reason:"timeout"}` to host, seat freed |
| Host socket drops | 60 s | `room_closed {reason:"host_timeout"}` to players |
| No messages in a room | 10 min | `room_closed {reason:"idle"}` |
| Missed heartbeat pong | 25–50 s (the next ping after the unanswered one) | Socket terminated (counts as a drop, so the player grace period starts) |
| Server shutdown (redeploy) | up to 2 s for clients to close | `room_closed {reason:"server_shutdown"}`, then close code `1001` |

---

## 6. Example flow

```
Unity  → {"t":"host_create"}
Unity  ← {"t":"room_created","room":"KXQM","hostKey":"9f3c…"}

Phone  → {"t":"join","room":"kxqm","name":"Harry"}
Phone  ← {"t":"joined","room":"KXQM","playerId":"p_a1b2","name":"Harry","rejoined":false}
Unity  ← {"t":"player_joined","playerId":"p_a1b2","name":"Harry"}

Unity  → {"t":"grant_broadcast","playerId":"p_a1b2"}
Unity  → {"t":"send","to":"p_a1b2","d":{"t":"your_target","hex":"#FF5733"}}
Phone  ← {"t":"msg","from":"host","d":{"t":"your_target","hex":"#FF5733"}}

Phone  → {"t":"send","to":"all","d":{"t":"stroke_batch","pts":[[0.1,0.2],[0.12,0.22]]}}
Unity  ← {"t":"msg","from":"p_a1b2","d":{"t":"stroke_batch","pts":[[0.1,0.2],[0.12,0.22]]}}
(every other phone receives the same)

-- phone loses signal --
Unity  ← {"t":"player_disconnected","playerId":"p_a1b2"}
Phone  → {"t":"join","room":"KXQM","name":"Harry","playerId":"p_a1b2"}
Phone  ← {"t":"joined","room":"KXQM","playerId":"p_a1b2","name":"Harry","rejoined":true}
Unity  ← {"t":"player_rejoined","playerId":"p_a1b2","name":"Harry"}
Unity  → {"t":"send","to":"p_a1b2","d":{"t":"snapshot", …}}
```

---

## 7. Game messages (`d`) — owned by Unity, not the relay

The relay forwards `d` untouched. The game layer should also use a `t` field inside `d`.
Suggested set (non-normative; the Unity team owns this list):

| `d.t` | Direction | Purpose |
|---|---|---|
| `state` | host → all | `phase`, `round`, `remainingMs`, players & scores |
| `your_target` | host → artist | Secret target hex — **only ever sent to the artist** |
| `picker_range` | host → each guesser | Two hex bounds for the guess picker |
| `stroke_batch` | artist → all | Normalized `[0..1]` points, sent every ~100 ms |
| `undo` / `clear` | artist → all | Canvas edits |
| `guess` | player → host | `hex` |
| `guess_submitted` | host → all | Feeds the bubble display (no ΔE yet) |
| `results` | host → all | Target, guesses sorted by ΔE, scores |
| `leaderboard` | host → all | Final standings |
| `snapshot` | host → one player | Full state for a rejoining player |

Send `remainingMs`, not absolute timestamps — device clocks are not in sync.

---

## 8. Client notes

**Unity (NativeWebSocket 2.x)** — implemented in `CodejamBackend/Assets/Scripts/Networking/RelayClient.cs`
- Version 2.x raises its events on Unity's main thread automatically. **No `DispatchMessageQueue()`
  needed** (that was version 1.x). Just create the `WebSocket` on the main thread (e.g. in `Start`).
- `await websocket.Connect()` only returns when the connection **closes** (it runs the receive loop),
  so don't put code after it expecting to run once connected; use `OnOpen`.
- `JsonUtility` cannot parse the arbitrary `d` object. Use Newtonsoft
  (`com.unity.nuget.newtonsoft-json`): parse to `JObject`, switch on `["t"]`.
- On unexpected close, reconnect and send `host_resume` with the saved `room` + `hostKey`.
  If it answers `host_active`, the relay hasn't noticed the old connection died yet: retry in a few seconds.
- **Keep the room alive:** a room with no messages for 10 minutes is closed (`room_closed {reason:"idle"}`).
  Heartbeat pings don't count, only real messages. `RelayClient` sends `{"t":"keepalive"}` to the
  players every 30 s; phones ignore it.

**React (browser `WebSocket`)**
- Pages is served over HTTPS, so the relay URL **must** be `wss://`.
- Save `playerId` and `room` in `localStorage`; on reconnect send `join` with the saved `playerId`.
- Reconnect on `close` unless the code is `4000` (kicked) or `4002` (replaced), or a
  `room_closed` was received.
