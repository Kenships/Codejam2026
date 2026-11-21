export const config = {
    port: Number(process.env.PORT ?? 8080),
    allowedOrigins: (process.env.ALLOWED_ORIGINS)?.split(',').map(x => x.trim()).filter((x) => x) ?? [],
    logLevel: process.env.LOG_LEVEL ?? "info",
    maxPayloadBytes: 64 * 1024
};