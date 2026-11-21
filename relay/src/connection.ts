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
    let identity: Identity = { kind: "none" };
    const fail = (code: ErrorCode) => conn.send({ t: "error", code });

    // ---- handlers, one per ClientMessage type ----

    function hostCreate() {
        if (identity.kind !== "none") return fail("already_in_room");
        const room = rooms.create(conn);
        identity = { kind: "host", room };
        conn.send({ t: "room_created", room: room.getCode(), hostKey: room.getHostKey() });
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
    }

    function send(msg: MsgOf<"send">) {
        if (identity.kind === "none") return fail("not_in_room");
        const route = resolveTargets(identity.room, identity, msg.to);
        if (!route.ok) return fail(route.code);

        // `from` is decided here, from who this socket really is. Never from the client.
        const from = identity.kind === "host" ? "host" : identity.playerId;
        const out: ServerMessage = { t: "msg", from, d: msg.d };
        for (const target of route.conns) target.send(out);
    }

    function grantBroadcast(msg: MsgOf<"grant_broadcast">) {
        if (identity.kind === "none") return fail("not_in_room");
        if (identity.kind !== "host") return fail("forbidden");
        if (!identity.room.setBroadcasterId(msg.playerId)) return fail("unknown_target");
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
            console.error("handler crashed", err);
        }
    });

    ws.on("close", () => {
        if (identity.kind === "player") {
            const { room, playerId } = identity;
            // false when this socket was already replaced (second tab) or kicked:
            // then the player isn't actually gone, so the host hears nothing.
            if (room.markDisconnected(playerId, conn)) {
                room.getHost()?.send({ t: "player_disconnected", playerId });
                // Hold the seat for a while; rejoin() cancels this.
                room.startPlayerGrace(playerId, timings.playerGraceMs, () => {
                    const seat = room.removePlayer(playerId);
                    if (seat) room.getHost()?.send({ t: "player_left", playerId, reason: "timeout" });
                });
            }
        } else if (identity.kind === "host") {
            const { room } = identity;
            if (room.getHost() === conn) {
                room.setHost(null);
                sendToPlayers(room, { t: "host_lost" });
                // Keep the room for a while; host_resume (setHost) cancels this.
                room.startHostGrace(timings.hostGraceMs, () => rooms.closeRoom(room, "host_timeout"));
            }
        }
    });

    // Without a listener, a socket error (e.g. an oversized frame) is an
    // unhandled 'error' event, which would crash the whole process.
    ws.on("error", (err) => {
        console.warn("socket error:", err.message);
    });
}
