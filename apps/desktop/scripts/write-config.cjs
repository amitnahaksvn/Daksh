"use strict";
// Sets the address built into a release: DAKSHA_DEFAULT_APP_URL=https://app.example.com npm run dist:win
const fs = require("node:fs");
const path = require("node:path");
const { normalizeAppUrl } = require("../src/config.cjs");

const file = path.join(__dirname, "..", "src", "app-config.json");
const raw = process.env.DAKSHA_DEFAULT_APP_URL;
if (raw) {
  const appUrl = normalizeAppUrl(raw);
  fs.writeFileSync(file, JSON.stringify({ appUrl }, null, 2) + "\n");
  console.log(`Release will open ${appUrl}`);
} else {
  console.log(`Release will open ${JSON.parse(fs.readFileSync(file, "utf8")).appUrl} (set DAKSHA_DEFAULT_APP_URL to change it)`);
}
