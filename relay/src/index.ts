import http from "node:http";
import { WebSocketServer } from "ws";
import { config } from "./config.ts";

const server = http.createServer((req, res) => {
    if (req.method === "GET" && req.url ==="/healthz") {
        res.statusCode = 200;
        res.setHeader('Content-type', 'application/json');
        res.end(JSON.stringify({"ok" : true, "uptime": process.uptime()}));
    } else {
        res.writeHead(404, {'Content-Type': 'text/plain'});
        res.end("404 Not Found: The requested page does not exist.");
    }
})

const wss = new WebSocketServer({ noServer: true, maxPayload: config.maxPayloadBytes });

server.on("upgrade", (req, socket, head) => {
    // TODO step 6: origin check
    wss.handleUpgrade(req, socket, head, (ws) => wss.emit("connection", ws, req));
})

wss.on("connection", (ws, req) => { 
    console.log("client connected", req.socket.remoteAddress);
    ws.on("message", (data) => ws.send(data.toString()));
    ws.on("close", (code) => {console.log("Disconnected", code)})
})

server.listen(config.port, () => console.log(`Listening on port ${config.port}`));