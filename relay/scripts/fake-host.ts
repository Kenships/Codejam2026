/**
 * Pretend to be the Unity host.
 *
 *   npm run host                       create a room on ws://localhost:8080
 *   npm run host -- wss://relay.app    create a room on another relay
 *   RELAY_URL=wss://... npm run host   same, via env
 *
 * Then type commands (see HELP). Players can be named by name or playerId.
 */
import WebSocket from "ws";
import { CLOSE_MEANINGS, DEFAULT_URL, log, parsePayload, splitCommand, startPrompt } from "./cli.ts";
import type { ServerMessage } from "../src/protocol.ts";

const url = process.argv[2] ?? DEFAULT_URL;

const HELP = `
Fake HOST commands (payload = a word like "state", or a JSON object):
  all <payload>              send to everyone          e.g.  all {"t":"state","phase":"DRAWING"}
  players <payload>          send to every player
  to <name|id> <payload>     send to one player        e.g.  to Harry {"t":"your_target","hex":"#FF5733"}
  grant <name|id>            let that player broadcast (the artist)
  grant none                 take broadcast permission away
  kick <name|id>             remove a player
  list                       show players
  drop                       cut the connection (like Unity crashing)
  resume                     reconnect with host_resume
  raw <json>                 send any JSON as-is
  help | quit
`;

let ws: WebSocket;
let room: string | undefined;
let hostKey: string | undefined;
/** playerId → { name, connected } */
const players = new Map<string, { name: string; connected: boolean }>();

function send(msg: Record<string, unknown>) {
    if (ws.readyState !== WebSocket.OPEN) return log("error", "not connected (try `resume`)");
    log("out", JSON.stringify(msg));
    ws.send(JSON.stringify(msg));
}

/** Accept a player's name (case-insensitive) or id; fall back to what was typed. */
function playerId(nameOrId: string): string {
    if (players.has(nameOrId)) return nameOrId;
    for (const [id, p] of players) if (p.name.toLowerCase() === nameOrId.toLowerCase()) return id;
    return nameOrId; // let the relay answer unknown_target
}

function connect(first: Record<string, unknown>) {
    ws = new WebSocket(url);
    ws.on("open", () => {
        log("info", `connected to ${url}`);
        send(first);
    });
    ws.on("message", (data) => {
        const msg = JSON.parse(data.toString()) as ServerMessage;
        log(msg.t === "error" ? "error" : "in", JSON.stringify(msg));
        switch (msg.t) {
            case "room_created":
                room = msg.room;
                hostKey = msg.hostKey;
                log("info", `ROOM CODE: ${room}    players join with:  npm run player -- ${room} <name>`);
                break;
            case "host_resumed":
                players.clear();
                for (const p of msg.players) players.set(p.playerId, { name: p.name, connected: p.connected });
                break;
            case "player_joined":
            case "player_rejoined":
                players.set(msg.playerId, { name: msg.name, connected: true });
                break;
            case "player_disconnected": {
                const p = players.get(msg.playerId);
                if (p) p.connected = false;
                break;
            }
            case "player_left":
                players.delete(msg.playerId);
                break;
        }
    });
    ws.on("close", (code, reason) => {
        log("info", `disconnected: ${code} ${CLOSE_MEANINGS[code] ?? ""} ${reason.toString()}`.trim());
        if (room) log("info", "type `resume` to reconnect to the same room");
    });
    ws.on("error", (err) => log("error", err.message));
}

startPrompt("host", HELP, (line) => {
    const cmd = line.split(/\s+/)[0] ?? "";
    switch (cmd) {
        case "all":
        case "players": {
            const [, rest = ""] = splitCommand(line, 2);
            const d = parsePayload(rest);
            if (d) send({ t: "send", to: cmd, d });
            break;
        }
        case "to": {
            const [, who = "", rest = ""] = splitCommand(line, 3);
            const d = parsePayload(rest);
            if (d) send({ t: "send", to: playerId(who), d });
            break;
        }
        case "grant": {
            const [, who = ""] = splitCommand(line, 2);
            send({ t: "grant_broadcast", playerId: who === "none" ? null : playerId(who) });
            break;
        }
        case "kick": {
            const [, who = ""] = splitCommand(line, 2);
            send({ t: "kick", playerId: playerId(who) });
            break;
        }
        case "list":
            if (players.size === 0) log("info", "no players yet");
            for (const [id, p] of players) log("info", `${p.name.padEnd(16)} ${id}  ${p.connected ? "connected" : "DISCONNECTED"}`);
            break;
        case "drop":
            ws.terminate();
            break;
        case "resume":
            if (!room || !hostKey) return log("error", "no room to resume yet");
            if (ws.readyState === WebSocket.OPEN) return log("error", "still connected; `drop` first");
            connect({ t: "host_resume", room, hostKey });
            break;
        case "raw": {
            const [, rest = ""] = splitCommand(line, 2);
            try {
                send(JSON.parse(rest));
            } catch {
                log("error", "raw needs valid JSON");
            }
            break;
        }
        default:
            log("error", `unknown command "${cmd}" (type help)`);
    }
});

connect({ t: "host_create" });
