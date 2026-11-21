import { type Room, type Conn } from "./room.ts";
import { type ErrorCode, type Target } from "./protocol.ts";

export type Identity =
    | { kind : "none" }
    | { kind: "host"; room: Room}
    | { kind: "player"; room: Room; playerId: string};

export type Sender = Exclude<Identity, { kind: "none"}>;

export type RouteResult = 
| { ok: true; conns: Conn[]} 
| { ok: false; code: ErrorCode}

export function resolveTargets(room : Room, sender : Sender, to : Target) : RouteResult {
    switch(sender.kind) {
        case "host":
            if (to === "host") {
                return { ok: false, code: "forbidden"};
            } else if (to === "all" || to === "players") {
                return { ok: true, conns: room.ConnectedPlayers().map(seat => seat.conn)}
            } else {
                const seat = room.getSeat(to);
                if (!seat) {
                    return { ok: false, code: "unknown_target"}
                }
                return { ok: true, conns: (seat.conn) ? [seat.conn] : []}
            }

        case "player":
            if (to === "host") {
                const host = room.getHost()
                return { ok: true, conns: host ? [host] : [] } 
            } else if (to === "all" || to === "players") {
                if (!room.canBroadcast(sender.playerId)) {
                    return { ok: false, code: "forbidden"}
                // player with permissions branch
                } else {
                    const others = room.ConnectedPlayers().filter(seat => seat.playerId != sender.playerId).map(seat => seat.conn)
                    if (to === "all") {
                        const host = room.getHost()
                        return { ok: true, conns: host ? [...others, host] : others}
                    } else {
                        return { ok: true, conns: others}
                    }
                }
            } else {
                return { ok: false, code: "forbidden" }
            }
        
        
    }
    
}