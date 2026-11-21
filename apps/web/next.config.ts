import type { NextConfig } from "next";

// STATIC_EXPORT=1 builds a plain static site (out/) for free static hosting; the default build is unchanged.
const staticExport = process.env.STATIC_EXPORT === "1";

const nextConfig: NextConfig = {
  ...(staticExport ? { output: "export" as const, trailingSlash: true, images: { unoptimized: true } } : {}),
  // Partial prerendering is not available in export mode.
  cacheComponents: !staticExport,
  partialPrefetching: !staticExport,
  experimental: {
    agentUpgrade: "security",
  },
  turbopack: {
    rules: {
      "*.css": {
        loaders: ["@tailwindcss/turbopack"],
        as: "*.css",
      },
    },
  },
};

export default nextConfig;
