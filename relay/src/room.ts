import { type ServerMessage, type ErrorCode, type PlayerInfo, NAME_MAX_LENGTH, MAX_PLAYERS } from "./protocol.ts";
import { generatePlayerId } from "./codes.ts";

export type Conn = {
    send(msg: ServerMessage): void;
    close(code: number, reason?: string): void;
}

export type AddPlayerResult = 
    | { ok : true; seat : Seat}
    | { ok: false; code: ErrorCode };

export type Seat = {
    playerId: string;
    name: string;
    conn: Conn | null;
}


export class Room {
    private code : string;
    private hostKey : string;
    private host : Conn | null;
    private seats : Map<string, Seat>;
    private broadcasterId: string | null;

    constructor(code : string, hostKey : string, host : Conn | null) {
        this.code = code;
        this.hostKey = hostKey;
        this.host = host;
        this.seats = new Map<string, Seat>()
        this.broadcasterId = null;
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
        const seat : Seat = { playerId: newPlayerId, name : trimmed, conn : conn};
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

}