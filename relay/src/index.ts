import { config } from "./config.ts";
import { startRelay } from "./server.ts";

const relay = await startRelay({ port: config.port });
console.log(`Listening on port ${relay.port}`);
if (config.allowedOrigins.length === 0) {
    console.warn("ALLOWED_ORIGINS is not set: accepting browser connections from any website.");
} else {
    console.log(`Allowed origins: ${config.allowedOrigins.join(", ")}`);
}

// Railway sends SIGTERM before stopping or redeploying the relay; Ctrl+C sends SIGINT.
// Close every room politely so clients see room_closed {server_shutdown}
// instead of a dropped connection, then exit.
let shuttingDown = false;
async function shutdown(signal: string) {
    if (shuttingDown) return; // a second Ctrl+C while we're already closing
    shuttingDown = true;
    console.log(`${signal} received: closing ${relay.rooms.size} room(s)`);
    await relay.shutdown();
    process.exit(0);
}

process.on("SIGTERM", () => void shutdown("SIGTERM"));
process.on("SIGINT", () => void shutdown("SIGINT"));
