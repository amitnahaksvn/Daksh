"use strict";
// Draws build/icon.png (1024x1024): a violet rounded square with a white "D". electron-builder turns it into .ico and .icns.
const fs = require("node:fs");
const path = require("node:path");
const zlib = require("node:zlib");

const N = 1024;
const px = Buffer.alloc(N * N * 4);
const clamp = (v) => Math.max(0, Math.min(1, v));

// Signed distance to a rounded square, then to the letter D (a bar plus a half ring), all smoothed over ~1.5px.
const sdRoundRect = (x, y, half, r) => { const qx = Math.abs(x) - half + r, qy = Math.abs(y) - half + r; return Math.hypot(Math.max(qx, 0), Math.max(qy, 0)) + Math.min(Math.max(qx, qy), 0) - r; };
const sdBox = (x, y, hx, hy) => { const qx = Math.abs(x) - hx, qy = Math.abs(y) - hy; return Math.hypot(Math.max(qx, 0), Math.max(qy, 0)) + Math.min(Math.max(qx, qy), 0); };

for (let j = 0; j < N; j++) {
  for (let i = 0; i < N; i++) {
    const x = i - N / 2 + 0.5, y = j - N / 2 + 0.5;
    const o = (j * N + i) * 4;
    const bg = clamp(0.5 - sdRoundRect(x, y, 460, 200) / 1.5);
    const t = clamp((x + y) / (2 * N) + 0.5);
    const r = 124 + (219 - 124) * t * 0.6, g = 58 + (39 - 58) * t * 0.6, b = 237 + (119 - 237) * t * 0.6;
    // D: vertical stem on the left, bowl as a ring clipped to the right half.
    const stem = sdBox(x + 150, y, 55, 250);
    const ringDist = Math.abs(Math.hypot(x + 95, y) - 195) - 55;
    const bowl = x > -95 ? ringDist : 1e9;
    const d = Math.min(stem, bowl);
    const fg = clamp(0.5 - d / 1.5);
    px[o] = Math.round(r * (1 - fg) + 255 * fg);
    px[o + 1] = Math.round(g * (1 - fg) + 255 * fg);
    px[o + 2] = Math.round(b * (1 - fg) + 255 * fg);
    px[o + 3] = Math.round(255 * bg);
  }
}

const crcTable = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
const crc = (buf) => { let c = 0xffffffff; for (const b of buf) c = crcTable[(c ^ b) & 255] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
const chunk = (type, data) => { const len = Buffer.alloc(4); len.writeUInt32BE(data.length); const td = Buffer.concat([Buffer.from(type), data]); const c = Buffer.alloc(4); c.writeUInt32BE(crc(td)); return Buffer.concat([len, td, c]); };

const raw = Buffer.alloc((N * 4 + 1) * N);
for (let j = 0; j < N; j++) { raw[j * (N * 4 + 1)] = 0; px.copy(raw, j * (N * 4 + 1) + 1, j * N * 4, (j + 1) * N * 4); }
const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(N, 0); ihdr.writeUInt32BE(N, 4); ihdr[8] = 8; ihdr[9] = 6;
const png = Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk("IHDR", ihdr), chunk("IDAT", zlib.deflateSync(raw, { level: 9 })), chunk("IEND", Buffer.alloc(0))]);
const out = path.join(__dirname, "..", "build", "icon.png");
fs.writeFileSync(out, png);
console.log(`Wrote ${out} (${png.length} bytes)`);
