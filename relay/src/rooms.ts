import { Room, type Conn } from "./room.ts";
import { generateHostKey, generateRoomCode } from "./codes.ts";

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
}

function normalize(code: string): string {
    return code.trim().toUpperCase();
}
