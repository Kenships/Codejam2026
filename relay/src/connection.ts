import { WebSocket } from "ws";
import type { Rooms } from "./rooms.ts";
import type { RateLimit, Timings } from "./config.ts";
import { TokenBucket } from "./rateLimit.ts";
import type { Conn, Room } from "./room.ts";
import {
    type ClientMessage,
    type ErrorCode,
    type ServerMessage,
    CloseCode,
    parseClientMessage,
} from "./protocol.ts";
import { resolveTargets, type Identity } from "./routing.ts";
import { log } from "./log.ts";

/** Running number per socket, so logs can follow one connection: conn=7. */
let nextConnId = 0;

/** The ClientMessage variant with this `t`, e.g. MsgOf<"join"> is Join. */
type MsgOf<T extends ClientMessage["t"]> = Extract<ClientMessage, { t: T }>;

function wrap(ws: WebSocket): Conn {
    return {
        send(msg: ServerMessage) {
            if (ws.readyState === WebSocket.OPEN) {
                ws.send(JSON.stringify(msg));
            }
        },
        close(code: number, reason?: string) {
            ws.close(code, reason);
        },
    };
}

/** Send one message to every connected player in the room (not the host). */
function sendToPlayers(room: Room, msg: ServerMessage) {
    for (const seat of room.ConnectedPlayers()) seat.conn.send(msg);
}

/**
 * Called once for every new WebSocket.
 * Everything below is per-socket: each socket gets its own conn and identity.
 */
export function handleConnection(ws: WebSocket, rooms: Rooms, timings: Timings, rateLimit: RateLimit): void {
    const conn = wrap(ws);
    const id = ++nextConnId;
    let identity: Identity = { kind: "none" };

    /** Log fields describing this socket: conn, plus room and who it is once known. */
    const who = () =>
        identity.kind === "none"
            ? { conn: id }
            : identity.kind === "host"
              ? { conn: id, room: identity.room.getCode(), role: "host" }
              : { conn: id, room: identity.room.getCode(), player: identity.playerId };

    const fail = (code: ErrorCode) => {
        log.debug("error sent", { ...who(), code });
        conn.send({ t: "error", code });
    };

    log.debug("socket opened", { conn: id });

    // ---- handlers, one per ClientMessage type ----

    function hostCreate() {
        if (identity.kind !== "none") return fail("already_in_room");
        const room = rooms.create(conn);
        identity = { kind: "host", room };
        conn.send({ t: "room_created", room: room.getCode(), hostKey: room.getHostKey() });
        log.info("room created", who());
    }

    function hostResume(msg: MsgOf<"host_resume">) {
        if (identity.kind !== "none") return fail("already_in_room");
        const room = rooms.get(msg.room);
        if (!room) return fail("room_not_found");
        if (!room.checkHostKey(msg.hostKey)) return fail("bad_host_key");
        if (room.getHost() !== null) return fail("host_active");

        room.setHost(conn);
        identity = { kind: "host", room };
        conn.send({ t: "host_resumed", room: room.getCode(), players: room.playerList() });
        sendToPlayers(room, { t: "host_back" });
        log.info("host resumed", { ...who(), players: room.playerList().length });
    }

    function join(msg: MsgOf<"join">) {
        if (identity.kind !== "none") return fail("already_in_room");
        const room = rooms.get(msg.room);
        if (!room) return fail("room_not_found");

        // Rejoin: reclaim an existing seat. The name in the message is ignored.
        if (msg.playerId !== undefined) {
            const rejoined = room.rejoin(msg.playerId, conn);
            if (rejoined) {
                const { seat, oldConn } = rejoined;
                oldConn?.close(CloseCode.Replaced, "replaced"); // e.g. the same game open in a second tab
                identity = { kind: "player", room, playerId: seat.playerId };
                conn.send({ t: "joined", room: room.getCode(), playerId: seat.playerId, name: seat.name, rejoined: true });
                room.getHost()?.send({ t: "player_rejoined", playerId: seat.playerId, name: seat.name });
                log.info("player rejoined", { ...who(), name: seat.name, replacedOpenSocket: oldConn !== null });
                return;
            }
            // Unknown or expired playerId: fall through and join as a new player.
        }

        const result = room.addPlayer(msg.name, conn);
        if (!result.ok) return fail(result.code);

        const { seat } = result;
        identity = { kind: "player", room, playerId: seat.playerId };
        conn.send({ t: "joined", room: room.getCode(), playerId: seat.playerId, name: seat.name, rejoined: false });
        room.getHost()?.send({ t: "player_joined", playerId: seat.playerId, name: seat.name });
        log.info("player joined", { ...who(), name: seat.name });
    }

    function send(msg: MsgOf<"send">) {
        if (identity.kind === "none") return fail("not_in_room");
        const route = resolveTargets(identity.room, identity, msg.to);
        if (!route.ok) return fail(route.code);

        // `from` is decided here, from who this socket really is. Never from the client.
        const from = identity.kind === "host" ? "host" : identity.playerId;
        const out: ServerMessage = { t: "msg", from, d: msg.d };
        for (const target of route.conns) target.send(out);
        // The payload itself is never logged, only its type and how far it went.
        log.debug("routed", { ...who(), to: msg.to, type: typeof msg.d.t === "string" ? msg.d.t : undefined, recipients: route.conns.length });
    }

    function grantBroadcast(msg: MsgOf<"grant_broadcast">) {
        if (identity.kind === "none") return fail("not_in_room");
        if (identity.kind !== "host") return fail("forbidden");
        if (!identity.room.setBroadcasterId(msg.playerId)) return fail("unknown_target");
        log.info("broadcast granted", { ...who(), to: msg.playerId });
    }

    function kick(msg: MsgOf<"kick">) {
        if (identity.kind === "none") return fail("not_in_room");
        if (identity.kind !== "host") return fail("forbidden");
        const seat = identity.room.removePlayer(msg.playerId);
        if (!seat) return fail("unknown_target");

        // The seat is already gone, so this socket's close event will find
        // nothing to disconnect and the host won't get player_disconnected.
        seat.conn?.close(CloseCode.Kicked, "kicked");
        conn.send({ t: "player_left", playerId: seat.playerId, reason: "kicked" });
        log.info("player kicked", { ...who(), player: seat.playerId, name: seat.name });
    }

    // ---- socket events ----

    // Per-socket rate limit. `limited` is true while we're dropping messages,
    // so a flood gets ONE rate_limited error, not one error per message.
    const bucket = new TokenBucket(rateLimit.burst, rateLimit.perSecond);
    let limited = false;

    ws.on("message", (data, isBinary) => {
        // Checked before parsing: junk costs a token too.
        if (!bucket.take()) {
            if (!limited) {
                limited = true;
                log.warn("rate limited", who());
                fail("rate_limited");
            }
            return;
        }
        limited = false;

        if (isBinary) {
            fail("bad_json");
            return;
        }

        const parsed = parseClientMessage(data.toString());
        if (!parsed.ok) {
            fail(parsed.code);
            return;
        }

        const msg = parsed.msg;
        // Any valid message counts as activity, for the idle-room sweep.
        if (identity.kind !== "none") identity.room.touch();
        try {
            switch (msg.t) {
                case "host_create":
                    hostCreate();
                    break;
                case "host_resume":
                    hostResume(msg);
                    break;
                case "join":
                    join(msg);
                    break;
                case "send":
                    send(msg);
                    break;
                case "grant_broadcast":
                    grantBroadcast(msg);
                    break;
                case "kick":
                    kick(msg);
                    break;
            }
        } catch (err) {
            // A bug in one handler must not crash the relay and end every game.
            log.error("handler crashed", { ...who(), type: msg.t }, err);
        }
    });

    ws.on("close", (code) => {
        log.debug("socket closed", { ...who(), code });
        // The room was closed on purpose (timeout, idle, shutdown) and is closing
        // its own sockets: nobody to notify, no seat to hold.
        if (identity.kind !== "none" && identity.room.isClosed()) return;
        if (identity.kind === "player") {
            const { room, playerId } = identity;
            // false when this socket was already replaced (second tab) or kicked:
            // then the player isn't actually gone, so the host hears nothing.
            if (room.markDisconnected(playerId, conn)) {
                room.getHost()?.send({ t: "player_disconnected", playerId });
                log.info("player disconnected", { ...who(), code });
                // Hold the seat for a while; rejoin() cancels this.
                room.startPlayerGrace(playerId, timings.playerGraceMs, () => {
                    const seat = room.removePlayer(playerId);
                    if (!seat) return;
                    room.getHost()?.send({ t: "player_left", playerId, reason: "timeout" });
                    log.info("player left", { room: room.getCode(), player: playerId, name: seat.name, reason: "timeout" });
                });
            }
        } else if (identity.kind === "host") {
            const { room } = identity;
            if (room.getHost() === conn) {
                room.setHost(null);
                sendToPlayers(room, { t: "host_lost" });
                log.info("host lost", { ...who(), code, graceMs: timings.hostGraceMs });
                // Keep the room for a while; host_resume (setHost) cancels this.
                room.startHostGrace(timings.hostGraceMs, () => rooms.closeRoom(room, "host_timeout"));
            }
        }
    });

    // Without a listener, a socket error (e.g. an oversized frame) is an
    // unhandled 'error' event, which would crash the whole process.
    ws.on("error", (err) => {
        log.warn("socket error", { ...who(), error: err.message });
    });
}
