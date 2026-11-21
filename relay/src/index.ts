import { config } from "./config.ts";
import { startRelay } from "./server.ts";
import { log } from "./log.ts";

const relay = await startRelay({ port: config.port });
log.info("relay listening", { port: relay.port, logLevel: config.logLevel });
if (config.allowedOrigins.length === 0) {
    log.warn("ALLOWED_ORIGINS is not set: accepting browser connections from any website");
} else {
    log.info("allowed origins", { origins: config.allowedOrigins.join(",") });
}

// Railway sends SIGTERM before stopping or redeploying the relay; Ctrl+C sends SIGINT.
// Close every room politely so clients see room_closed {server_shutdown}
// instead of a dropped connection, then exit.
let shuttingDown = false;
async function shutdown(signal: string) {
    if (shuttingDown) return; // a second Ctrl+C while we're already closing
    shuttingDown = true;
    log.info("shutting down", { signal, rooms: relay.rooms.size });
    await relay.shutdown();
    process.exit(0);
}

process.on("SIGTERM", () => void shutdown("SIGTERM"));
process.on("SIGINT", () => void shutdown("SIGINT"));
