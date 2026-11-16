// Must be the first thing index.ts imports, so process.env is populated before config.ts reads it.
try {
  process.loadEnvFile();
} catch {
  // No .env file (e.g. production, where real env vars are injected by the host) — fine to skip.
}
