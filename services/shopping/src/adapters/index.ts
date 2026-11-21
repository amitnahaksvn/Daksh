import { amazon } from "./amazon.js";
import { flipkart } from "./flipkart.js";
import type { Adapter } from "./types.js";

export const adapters: Record<string, Adapter> = { amazon, flipkart };
export * from "./types.js";
