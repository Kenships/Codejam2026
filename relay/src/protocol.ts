export const ROOM_CODE_ALPHABET = "ABCDEFGHJKMNPQRSTUVWXYZ";
export const ROOM_CODE_LENGTH = 4;
export const MAX_PLAYERS = 12;
export const NAME_MAX_LENGTH = 16;

export type ErrorCode = (
    | "bad_json" 
    | "bad_message" 
    | "not_in_room" 
    | "already_in_room"
    | "room_not_found"
    | "room_full"
    | "name_invalid"
    | "name_taken"
    | "bad_host_key"
    | "host_active"
    | "forbidden"
    | "unknown_target"
    | "rate_limited"
);

export const CloseCode = { Kicked: 4000, Replaced: 4002} as const;


// <---- Client -> Relay ----/>
export type HostCreate = {
    t: "host_create";
}

export type HostResume = {
    t: "host_resume";
    room: string;
    hostKey: string;
}

export type Join = {
    t: "join";
    room: string;
    name: string;
    playerId?: string;
};

export type Target = "host" | "players" | "all"| (string & {});

export type Send = {
    t: "send";
    to: Target;
    d: Record<string, unknown>;
};

export type GrantBroadcast = {
    t: "grant_broadcast";
    playerId: string | null;
};

export type Kick = {
    t: "kick";
    playerId: string;
}

export type ClientMessage = (
    | HostCreate
    | HostResume
    | Join
    | Send
    | GrantBroadcast
    | Kick
)


// // <---- Relay -> Client ----/>

export type PlayerInfo = {
    playerId: string;
    name: string;
    connected: boolean;
};

/** Reply to host_create. hostKey is secret: only ever sent to the host. */
export type RoomCreated = {
    t: "room_created";
    room: string;
    hostKey: string;
};

/** Reply to a successful host_resume, with the current seat list. */
export type HostResumed = {
    t: "host_resumed";
    room: string;
    players: PlayerInfo[];
};

/** Reply to join. rejoined is true when an existing seat was reclaimed. */
export type Joined = {
    t: "joined";
    room: string;
    playerId: string;
    name: string;
    rejoined: boolean;
};

/** A forwarded `send`. `from` is always stamped by the relay, never copied from the client. */
export type Msg = {
    t: "msg";
    from: "host" | (string & {});
    d: Record<string, unknown>;
};

// ---- Host-only notifications ----

export type PlayerJoined = {
    t: "player_joined";
    playerId: string;
    name: string;
};

export type PlayerRejoined = {
    t: "player_rejoined";
    playerId: string;
    name: string;
};

/** Socket dropped; seat is held for the grace period. */
export type PlayerDisconnected = {
    t: "player_disconnected";
    playerId: string;
};

/** Seat released for good. */
export type PlayerLeft = {
    t: "player_left";
    playerId: string;
    reason: "timeout" | "kicked";
};

// ---- Player-only notifications ----

export type HostLost = {
    t: "host_lost";
};

export type HostBack = {
    t: "host_back";
};

// ---- Everyone ----

export type RoomClosed = {
    t: "room_closed";
    reason: "host_timeout" | "idle" | "server_shutdown";
};

export type ErrorMsg = {
    t: "error";
    code: ErrorCode;
    message?: string;
};

export type ServerMessage = (
    | RoomCreated
    | HostResumed
    | Joined
    | Msg
    | PlayerJoined
    | PlayerRejoined
    | PlayerDisconnected
    | PlayerLeft
    | HostLost
    | HostBack
    | RoomClosed
    | ErrorMsg
);

// <---- Parser Helpers ----/>
function isPlainObject(v: unknown): v is Record<string, unknown> {
      if (typeof v !== 'object' || v === null) {
        return false;
    } else {
        return !Array.isArray(v);
    }
}   
// <---- Parser ----/>

export type ParseResult =
    | { ok: true; msg: ClientMessage }
    | { ok: false; code: ErrorCode; message: string}

/** Shorthand for a bad_message failure. */
function bad(message: string): ParseResult {
    return { ok: false, code: "bad_message", message };
}

export function parseClientMessage(raw: string): ParseResult {
    // Declared outside the try so the code after catch can use it.
    // The try only wraps JSON.parse, so a bug in the switch below
    // can't be mistaken for bad JSON.
    let parsed: unknown;
    try {
        parsed = JSON.parse(raw);
    } catch {
        return { ok: false, code: "bad_json", message: "Failed to parse" };
    }

    if (!isPlainObject(parsed)) {
        return { ok: false, code: "bad_json", message: "Message must be a JSON object" };
    }

    // From here on, parsed is Record<string, unknown>: every field is `unknown`
    // until we check its type. Each case builds a NEW object with only the known
    // fields, so extra fields a client sneaks in (e.g. "from") are dropped.
    switch (parsed.t) {
        case "host_create":
            return { ok: true, msg: { t: "host_create" } };

        case "host_resume": {
            const { room, hostKey } = parsed;
            if (typeof room !== "string" || typeof hostKey !== "string") {
                return bad("host_resume needs string fields: room, hostKey");
            }
            return { ok: true, msg: { t: "host_resume", room, hostKey } };
        }

        case "join": {
            const { room, name, playerId } = parsed;
            if (typeof room !== "string" || typeof name !== "string") {
                return bad("join needs string fields: room, name");
            }
            if (playerId !== undefined && typeof playerId !== "string") {
                return bad("join.playerId must be a string when present");
            }
            return { ok: true, msg: { t: "join", room, name, playerId } };
        }

        case "send": {
            const { to, d } = parsed;
            if (typeof to !== "string" || to.length === 0) {
                return bad("send.to must be a non-empty string");
            }
            if (!isPlainObject(d)) {
                return bad("send.d must be a JSON object");
            }
            return { ok: true, msg: { t: "send", to, d } };
        }

        case "grant_broadcast": {
            const { playerId } = parsed;
            if (playerId !== null && typeof playerId !== "string") {
                return bad("grant_broadcast.playerId must be a string or null");
            }
            return { ok: true, msg: { t: "grant_broadcast", playerId } };
        }

        case "kick": {
            const { playerId } = parsed;
            if (typeof playerId !== "string") {
                return bad("kick.playerId must be a string");
            }
            return { ok: true, msg: { t: "kick", playerId } };
        }

        default:
            return bad("Unknown message type");
    }
}