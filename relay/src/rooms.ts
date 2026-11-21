import { Room, type Conn } from "./room.ts";
import { generateHostKey, generateRoomCode } from "./codes.ts";
import type { RoomClosed } from "./protocol.ts";

/** Every live room, keyed by its 4-letter code. */
export class Rooms {
    private readonly rooms = new Map<string, Room>();

    /** Make a room for a newly connected host, with a fresh unique code and host key. */
    create(host: Conn): Room {
        const code = generateRoomCode((candidate) => this.rooms.has(candidate));
        const room = new Room(code, generateHostKey(), host);
        this.rooms.set(code, room);
        return room;
    }

    /** Look up a room. Codes are case-insensitive, so "kxqm" finds "KXQM". */
    get(code: string): Room | undefined {
        return this.rooms.get(normalize(code));
    }

    /** Returns true if a room was removed. */
    delete(code: string): boolean {
        return this.rooms.delete(normalize(code));
    }

    get size(): number {
        return this.rooms.size;
    }

    /** All rooms, for cleanup sweeps and shutdown (step 5). */
    values(): IterableIterator<Room> {
        return this.rooms.values();
    }

    /**
     * Shut a room down for good: stop its timers, tell everyone why,
     * close their sockets, and forget the room.
     */
    closeRoom(room: Room, reason: RoomClosed["reason"]): void {
        room.shutdown();
        // 1001 "going away" for a server shutdown, 1000 "normal" otherwise.
        const closeCode = reason === "server_shutdown" ? 1001 : 1000;
        const notice: RoomClosed = { t: "room_closed", reason };

        const everyone = room.ConnectedPlayers().map((seat) => seat.conn);
        const host = room.getHost();
        if (host) everyone.push(host);

        for (const conn of everyone) {
            conn.send(notice);
            conn.close(closeCode, reason);
        }
        this.rooms.delete(room.getCode());
    }

    /** Close every room that has been silent for longer than idleMs. */
    closeIdleRooms(idleMs: number, now = Date.now()): void {
        // Copy first: closeRoom deletes from the map we'd be looping over.
        for (const room of [...this.rooms.values()]) {
            if (now - room.getLastActivity() > idleMs) this.closeRoom(room, "idle");
        }
    }

    /** Close every room (server shutdown, or tests tearing down). */
    closeAll(reason: RoomClosed["reason"]): void {
        for (const room of [...this.rooms.values()]) this.closeRoom(room, reason);
    }
}

function normalize(code: string): string {
    return code.trim().toUpperCase();
}
