/**
 * Pretend to be a player's phone.
 *
 *   npm run player -- KXQM Harry                     join room KXQM as Harry
 *   npm run player -- KXQM Harry wss://relay.app     on another relay
 *
 * Then type commands (see HELP).
 */
import WebSocket from "ws";
import { CLOSE_MEANINGS, DEFAULT_URL, log, parsePayload, splitCommand, startPrompt } from "./cli.ts";
import type { ServerMessage } from "../src/protocol.ts";

const [room, name, url = DEFAULT_URL] = process.argv.slice(2);
if (!room || !name) {
    console.error("usage: npm run player -- <ROOM> <NAME> [relay url]");
    process.exit(1);
}

const HELP = `
Fake PLAYER commands (payload = a word like "ready", or a JSON object):
  host <payload>       send to the host           e.g.  host {"t":"guess","hex":"#FF5733"}
  all <payload>        broadcast to everyone (needs the host's grant)
  players <payload>    broadcast to other players (needs the grant)
  drop                 cut the connection (like losing signal)
  rejoin               reconnect, reclaiming the same seat
  tab                  open a SECOND connection with the same playerId (the first gets 4002)
  raw <json>           send any JSON as-is
  help | quit
`;

let ws: WebSocket;
let playerId: string | undefined;

function send(msg: Record<string, unknown>) {
    if (ws.readyState !== WebSocket.OPEN) return log("error", "not connected (try `rejoin`)");
    log("out", JSON.stringify(msg));
    ws.send(JSON.stringify(msg));
}

function connect() {
    const socket = new WebSocket(url);
    ws = socket;
    socket.on("open", () => {
        log("info", `connected to ${url}`);
        // With a saved playerId this is a rejoin; the relay keeps our seat and name.
        send(playerId ? { t: "join", room, name, playerId } : { t: "join", room, name });
    });
    socket.on("message", (data) => {
        const msg = JSON.parse(data.toString()) as ServerMessage;
        log(msg.t === "error" ? "error" : "in", JSON.stringify(msg));
        if (msg.t === "joined") {
            playerId = msg.playerId;
            log("info", `${msg.rejoined ? "rejoined" : "joined"} ${msg.room} as ${msg.name} (playerId ${msg.playerId})`);
        }
    });
    socket.on("close", (code, reason) => {
        // Only report the socket we're currently using; an old one closing
        // after `tab` is expected.
        const label = socket === ws ? "disconnected" : "old connection closed";
        log("info", `${label}: ${code} ${CLOSE_MEANINGS[code] ?? ""} ${reason.toString()}`.trim());
        if (socket === ws && code !== 4000 && playerId) log("info", "type `rejoin` to reconnect");
    });
    socket.on("error", (err) => log("error", err.message));
}

startPrompt(name, HELP, (line) => {
    const cmd = line.split(/\s+/)[0] ?? "";
    switch (cmd) {
        case "host":
        case "all":
        case "players": {
            const [, rest = ""] = splitCommand(line, 2);
            const d = parsePayload(rest);
            if (d) send({ t: "send", to: cmd, d });
            break;
        }
        case "drop":
            ws.terminate();
            break;
        case "rejoin":
            if (ws.readyState === WebSocket.OPEN) return log("error", "still connected; `drop` first");
            connect();
            break;
        case "tab":
            if (!playerId) return log("error", "not joined yet");
            connect(); // the old socket stays open until the relay replaces it
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

connect();
