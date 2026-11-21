import crypto from "node:crypto";
import { ROOM_CODE_ALPHABET, ROOM_CODE_LENGTH } from "./protocol.ts";

export function generateRoomCode(isTaken : (code: string) => boolean) : string {
    let attempts : number = 0;
    let code : string = "";

    while (attempts < 100) {
        code = "";
        for (let i = 0; i < ROOM_CODE_LENGTH; i++) {
            code += ROOM_CODE_ALPHABET[crypto.randomInt(ROOM_CODE_ALPHABET.length)];
        }
        if (!isTaken(code)) {
            return code;
        }
        attempts++;
    }

    throw new Error("Could not generate room code");
}

export function generatePlayerId() {
    return crypto.randomBytes(5).toString("base64url");
}

export function generateHostKey() {
    return crypto.randomBytes(64).toString("base64url");
}