import { type ServerMessage, type ErrorCode, type PlayerInfo, NAME_MAX_LENGTH, MAX_PLAYERS } from "./protocol.ts";
import { generatePlayerId } from "./codes.ts";

export type Conn = {
    send(msg: ServerMessage): void;
    close(code: number, reason?: string): void;
}

export type AddPlayerResult = 
    | { ok : true; seat : Seat}
    | { ok: false; code: ErrorCode };

/** A message the host asked the relay to remember, and when (Date.now()) it was sent. */
export type Retained = { d: Record<string, unknown>; at: number };

export type Seat = {
    playerId: string;
    name: string;
    conn: Conn | null;
    /** Running while the player is disconnected; removes them when it fires. */
    graceTimer: NodeJS.Timeout | null;
    /** The host's retained message for this player only, replayed when they rejoin. */
    retained: Retained | null;
}


export class Room {
    private code : string;
    private hostKey : string;
    private host : Conn | null;
    private seats : Map<string, Seat>;
    private broadcasterId: string | null;
    /** Running while the host is disconnected; closes the room when it fires. */
    private hostGraceTimer: NodeJS.Timeout | null;
    /** Date.now() of the last message anyone in this room sent. */
    private lastActivity: number;
    /** Once true, the room is finished and refuses to start new timers. */
    private closed = false;
    /** The host's retained room-wide message, replayed to every join and rejoin. */
    private retained: Retained | null = null;

    constructor(code : string, hostKey : string, host : Conn | null) {
        this.code = code;
        this.hostKey = hostKey;
        this.host = host;
        this.seats = new Map<string, Seat>()
        this.broadcasterId = null;
        this.hostGraceTimer = null;
        this.lastActivity = Date.now();
    }

    addPlayer(name : string, conn : Conn) : AddPlayerResult {
        const trimmed = name.trim();
        if (this.seats.size >= MAX_PLAYERS) {
            return { ok: false, code: "room_full"}
        }
        if (trimmed.length <= 0 || trimmed.length > NAME_MAX_LENGTH) {
            return { ok: false, code: "name_invalid"}
        }
        for (const seat of this.seats.values()) {
            if (seat.name.toLowerCase() === trimmed.toLowerCase()) {
                return { ok: false, code: "name_taken"}
            }
        }


        const newPlayerId = generatePlayerId();
        const seat : Seat = { playerId: newPlayerId, name : trimmed, conn : conn, graceTimer: null, retained: null };
        this.seats.set(newPlayerId, seat);
        return { ok: true, seat};
    }

    rejoin(playerId : string, conn : Conn)  {
        const seat = this.seats.get(playerId);
        if (!seat) {
            return null
        }
        
        const oldConn = seat.conn;
        seat.conn = conn;
        this.cancelPlayerGrace(seat);
        return { seat, oldConn};
    }

    markDisconnected(playerId : string, conn : Conn) : boolean {
        const seat = this.seats.get(playerId);
        if (seat && seat.conn === conn) {
            seat.conn = null;
            return true;
        }
        return false;
    }

    removePlayer(playerId : string) {
        const deletedSeat = this.seats.get(playerId);
        if (deletedSeat) this.cancelPlayerGrace(deletedSeat);
        this.seats.delete(playerId);
        if (this.broadcasterId === playerId) {
            this.broadcasterId = null;
        }

        return deletedSeat;
    }

    getSeat(playerId : string) {
        return this.seats.get(playerId);
    }

    playerList() : PlayerInfo[] {
        const list : PlayerInfo[] = [];

        for ( const seat of this.seats.values()) {
            let player : PlayerInfo = {
                playerId : seat.playerId,
                name : seat.name,
                connected : seat.conn != null
            };
            list.push(player);
        }
        
        return list;
    }

    checkHostKey(key : string) {
        return key === this.hostKey;
    }

    getCode() {
        return this.code;
    }

    getHost() {
        return this.host;
    }

    setHost(conn : Conn | null) {
        this.host = conn;
        if (conn !== null) this.cancelHostGrace();
    }

    getBroadcasterId() {
        return this.broadcasterId;
    }

    canBroadcast(playerId : string | null) : boolean {
        if (playerId) {
            return (playerId === this.broadcasterId)
        } else {
            return false
        }
    }

    setBroadcasterId(playerId : string | null) : boolean {
        if (playerId == null) {
            this.broadcasterId = null
            return true;
        }

        if (this.seats.get(playerId) != null) {
            this.broadcasterId = playerId;
            return true;
        }
        
        return false;
    }

    ConnectedPlayers() : (Seat & { conn: Conn})[] {
        return [...this.seats.values()].filter((seat): seat is Seat & { conn: Conn } => seat.conn !== null,);
    }

    getHostKey() {
        return this.hostKey
    }

    // ---- retained (catch-up) messages ----
    // The room never looks inside these; it only stores the latest one per slot.
    // An empty object clears the slot.

    setRetained(d : Record<string, unknown>) {
        this.retained = Object.keys(d).length > 0 ? { d, at: Date.now() } : null;
    }

    getRetained() {
        return this.retained;
    }

    /** False when there's no such seat. */
    setSeatRetained(playerId : string, d : Record<string, unknown>) : boolean {
        const seat = this.seats.get(playerId);
        if (!seat) return false;
        seat.retained = Object.keys(d).length > 0 ? { d, at: Date.now() } : null;
        return true;
    }

    // ---- activity ----

    /** Record that someone in the room just sent a message. */
    touch() {
        this.lastActivity = Date.now();
    }

    getLastActivity() {
        return this.lastActivity;
    }

    // ---- grace timers ----
    // The room only stores and cancels timers. What happens when one fires
    // is decided by the caller (connection.ts), passed in as onExpire.

    /** Start (or restart) a dropped player's countdown. */
    startPlayerGrace(playerId : string, ms : number, onExpire : () => void) {
        if (this.closed) return;
        const seat = this.seats.get(playerId);
        if (!seat) return;
        this.cancelPlayerGrace(seat); // never two timers for one seat
        seat.graceTimer = setTimeout(() => {
            seat.graceTimer = null;
            onExpire();
        }, ms);
    }

    private cancelPlayerGrace(seat : Seat) {
        if (seat.graceTimer) {
            clearTimeout(seat.graceTimer);
            seat.graceTimer = null;
        }
    }

    /** Start (or restart) the countdown after the host drops. */
    startHostGrace(ms : number, onExpire : () => void) {
        if (this.closed) return;
        this.cancelHostGrace();
        this.hostGraceTimer = setTimeout(() => {
            this.hostGraceTimer = null;
            onExpire();
        }, ms);
    }

    private cancelHostGrace() {
        if (this.hostGraceTimer) {
            clearTimeout(this.hostGraceTimer);
            this.hostGraceTimer = null;
        }
    }

    /**
     * Mark the room finished and stop every countdown. Afterwards no new
     * timers can start, even from close events of sockets we're closing.
     */
    shutdown() {
        this.closed = true;
        this.cancelHostGrace();
        for (const seat of this.seats.values()) this.cancelPlayerGrace(seat);
    }

    isClosed() {
        return this.closed;
    }

}