# HueSlider Relay Protocol

**Version:** 1.1 (adds `send.retain` catch-up messages; documents the category pick)
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
| Rate limit | Per socket: bursts of up to 100 messages, refilling at 40/second. Over the limit, messages are dropped. The sender gets **one** `error: rate_limited` when it first goes over, then further drops are silent until it slows down. Every frame counts, including invalid ones. |
| Origin | Browser connections must come from an allowed origin (`ALLOWED_ORIGINS`), otherwise the socket is closed with `1008 "origin not allowed"`. Clients that send no `Origin` header (Unity) are always allowed. |
| Room size | 1 host + max **32 players** (the host can lower this with `set_max_players`). Disconnected players still hold a seat (and count) until their grace period ends. |
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
| `send` | Anyone in a room | `to: Target`, `d: object`, `retain?: boolean` (host only) | *(none — forwarded)* |
| `grant_broadcast` | Host | `playerId: string \| null` | *(none)* |
| `kick` | Host | `playerId: string` | *(none — host gets `player_left`)* |
| `leave` | Player | — | *(none — host gets `player_left {reason:"left"}`)* |
| `set_max_players` | Host | `maxPlayers: integer 1–32` | *(none)* — later joins over the limit get `room_full`. Nobody already seated is removed. A new room starts at 32. |

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

### Retained messages (catch-up)

The host can add `"retain": true` to a `send`. The relay delivers it as normal **and remembers it**,
so phones that arrive later catch up instantly, even while Unity is still reacting to the join (or
the host is briefly disconnected). The relay still never looks inside `d`.

| `to` with `retain: true` | Slot it fills | Replayed to |
|---|---|---|
| `"players"` / `"all"` | The room's slot | Every player who **joins or rejoins** from now on |
| `"<playerId>"` | That player's seat slot | That player only, when they **rejoin** |

- Each slot holds **one** message: a newer retained send to the same slot replaces it.
  Send `d: {}` with `retain: true` to clear a slot (nothing is delivered for an empty `d`).
- Replay happens immediately after `joined`: room slot first, then the seat slot. Both arrive as a
  normal `msg` from `"host"` plus `catchUp: true` and `ageMs` (how long ago the host sent it).
  **Subtract `ageMs` from any `remainingMs` inside `d`.**
- A seat slot is freed with the seat (timeout or kick). A brand-new player has an empty seat slot.
- `retain` from a player (even one holding the broadcast grant) → `error: forbidden`, nothing sent.
- Retained messages are **in addition to**, not instead of, the host reacting to
  `player_joined` / `player_rejoined`. They cover the gap until it does.

### Join / rejoin

- `join` **without** `playerId` → new seat, new `playerId`.
- `join` **with** a `playerId` that holds a seat in that room → rejoin that seat (name is kept from
  the original join; the `name` field is ignored). If that seat still has a live socket, the old
  socket is closed with code `4002` (replaced) — this handles "opened the game in a second tab".
- `join` with an unknown/expired `playerId` → treated as a new join.
- A dropped player's seat (and **name**) is held for the grace period (§5), so a *new* join with
  that name gets `name_taken` until it expires. A player leaving on purpose should send `leave`
  before closing: the seat and name are freed at once and the socket's close is ignored.

---

## 4. Relay → Client

| `t` | Recipient | Fields | When |
|---|---|---|---|
| `room_created` | Host | `room`, `hostKey` | After `host_create` |
| `host_resumed` | Host | `room`, `players: PlayerInfo[]` | After a successful `host_resume` |
| `joined` | Player | `room`, `playerId`, `name`, `rejoined: boolean` | After `join` |
| `msg` | Anyone | `from: "host" \| <playerId>`, `d: object`, `catchUp?: true`, `ageMs?: number` | A forwarded `send`, or a retained one replayed after `joined` (then `catchUp`/`ageMs` are set) |
| `player_joined` | Host | `playerId`, `name` | New player took a seat |
| `player_rejoined` | Host | `playerId`, `name` | Player reconnected to their seat |
| `player_disconnected` | Host | `playerId` | Player's socket dropped; seat is held for 30 s |
| `player_left` | Host | `playerId`, `reason: "timeout" \| "kicked" \| "left"` | Seat released for good |
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
| `not_in_room` | `send`/`grant_broadcast`/`kick`/`leave`/`set_max_players` before creating or joining |
| `already_in_room` | `host_create`/`host_resume`/`join` on a socket that already has an identity |
| `room_not_found` | No room with that code |
| `room_full` | Room already has as many players as its limit (32, or what the host set with `set_max_players`) |
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

-- game in progress; Unity keeps the shared phase retained --
Unity  → {"t":"send","to":"players","retain":true,"d":{"t":"state","phase":"drawing","round":1,"remainingMs":60000}}
-- 20 s later a new phone joins mid-game --
Phone  → {"t":"join","room":"KXQM","name":"Sam"}
Phone  ← {"t":"joined","room":"KXQM","playerId":"p_c3d4","name":"Sam","rejoined":false}
Phone  ← {"t":"msg","from":"host","catchUp":true,"ageMs":20012,"d":{"t":"state","phase":"drawing","round":1,"remainingMs":60000}}
         (phone shows ≈ 40 s left)
Unity  ← {"t":"player_joined","playerId":"p_c3d4","name":"Sam"}
Unity  → {"t":"send","to":"p_c3d4","d":{ …whatever Sam's screen needs… }}
```

---

## 7. Game messages (`d`) — owned by Unity, not the relay

The relay forwards `d` untouched; every `d` has its own `t`. This is the set the Unity host and
the phones use today, plus the **category pick** (new, marked ★). Unity owns this list.

### Phases (`state.phase`)

`waiting` (lobby) → `drawing` → `playing` (guessing one drawing) → `results` → … next drawing …
→ `finished`. Everyone draws at once in `drawing`; `playing`/`results` repeat once per drawing.

### Host → phone

| `d.t` | To | Fields | Notes |
|---|---|---|---|
| `state` | players or one player | `phase`, `round`, `remainingMs` | Send `remainingMs`, never timestamps (device clocks differ). **Retain it** when broadcast. In `drawing`, the phone runs its drawing timer from `remainingMs` (the host picks 30–120 s). |
| `your_target` | one player | `hex`, ★`categories: string[]` (exactly 4), ★`category: string \| null` | Sent at the start of `drawing`. `hex` is secret, only ever sent to its owner. `category` is `null` until they pick, then the pick (so a rejoining phone skips the picker). |
| `round_drawing` | each guesser (not the artist) | `image` (base64, see [Drawing images](#drawing-images)), `mime`, `prompt`, `artistName` | The drawing to guess, forwarded exactly as the artist submitted it. ★`prompt` is now the artist's **picked category**. |
| `results` | one player | `targetHex`, `roundPoints`, `totalPoints`, `roundRank`, `overallRank`, `totalPlayers` | Sent before the `results` state. |
| `keepalive` | players | — | Every 30 s so the room isn't closed as idle. Phones ignore it. |

### Phone → host

| `d.t` | Fields | Notes |
|---|---|---|
| `pfp_submit` | `image` (base64), `mime`, `compression`, `w`, `h` | Profile picture, lobby. Same format as `drawing_submit`. |
| ★`category_pick` | `category: string` | One of the 4 from `your_target.categories`. Send once, before drawing. |
| `stroke_batch` / `undo` / `clear` | — | Live canvas, to `"host"`. |
| `drawing_submit` | `prompt`, `image`, `mime`, `compression`, `w`, `h` | ★ `prompt` = the picked category. The host trusts its own record of the pick, not this field. |
| `guess` | `hex` | During `playing`. |

### Drawing images

Drawings and profile pictures are white ink on black, so `image` is a **1-bit bitmap**, not a PNG
(`mime: "image/x-truehue-bitmap"`). Before base64 the bytes are gzipped (`compression: "gzip"`;
`"none"` only if the browser has no `CompressionStream`). Once un-gzipped:

| Bytes | Meaning |
|---|---|
| 0–2 | Magic `TH1` |
| 3–4 | Width, uint16 little-endian (≤ 512) |
| 5–6 | Height, uint16 little-endian (≤ 512) |
| 7… | `ceil(w*h/8)` bytes of pixels, row-major from the **top-left**, most significant bit first. `1` = ink (white), `0` = background (black). |

A 512×512 bitmap is 32 KB before gzip, so it fits the frame limit even uncompressed; gzipped it's
usually a few KB. Decoders sniff the gzip header (`1f 8b`) and the `TH1` magic rather than trusting
`mime`/`compression`, and still accept the old gzipped PNG/JPEG payloads (e.g. a profile picture
saved by an older build). Reference code: `frontend/src/lib/drawingCodec.ts` and
`CodejamBackend/Assets/Scripts/Networking/DrawingImageCodec.cs`.

### ★ Category pick flow

```
Unity → player   {"t":"your_target","hex":"#3FA7D6","categories":["Animal","Food","Place","Weather"],"category":null}
Unity → players  {"t":"state","phase":"drawing","round":1,"remainingMs":60000}     (retain: true)
phone            shows the 4 categories → player taps one → canvas opens with it as the prompt
player → host    {"t":"category_pick","category":"Food"}
Unity            checks it's one of the 4 offered (ignore otherwise; later picks ignored), stores it
… drawing …
player → host    {"t":"drawing_submit","prompt":"Food", …}
Unity → guessers {"t":"round_drawing","prompt":"Food","artistName":"Harry", …}
```

- No pick by the end of drawing (or the grace period): Unity picks one of the 4 itself.
- Resync (`player_rejoined` during `drawing`): resend `your_target` with `category` set to the
  stored pick, or `null` if they haven't picked yet.

### Catching up players who join or rejoin mid-game

1. **Unity retains every shared `state`**: `send` to `"players"` with `retain: true`. Any phone that
   (re)joins immediately gets the current phase (with `ageMs` to correct `remainingMs`).
2. **On `player_rejoined`** Unity resends that player's own screen (as today): `drawing` →
   `your_target` (+ `category`) and `state`; `playing` → `round_drawing` (skip if they're the artist)
   and `state`; `results` → their `results`; `finished` → final `results` and `state`.
3. **On `player_joined` while a game is running** (the new part): add them to the session with
   0 points, then send their screen the same way as a rejoin. They can't draw this game if
   `drawing` is already underway, so: during `drawing` send `state` `waiting` to them only (they
   join in from the first `playing`); during `playing` send them the current `round_drawing`;
   during `results` they wait for the next drawing. They never count as a missing drawing.
4. **Phones**: treat a `catchUp` message exactly like a live one, after subtracting `ageMs` from
   `remainingMs`. A phone in `drawing` with no `your_target` shows a waiting screen.

Optional: Unity can also retain a player's own message (`to: "<playerId>"`, `retain: true`) so a
rejoining phone is restored even if the host is momentarily away. The slot holds only one message,
so use it for the one that matters most (e.g. `your_target` during `drawing`).

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
