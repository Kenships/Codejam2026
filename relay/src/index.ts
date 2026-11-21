import { config } from "./config.ts";
import { startRelay } from "./server.ts";

const relay = await startRelay(config.port);
console.log(`Listening on port ${relay.port}`);
