import { describe, expect, it } from "vitest";
import { splitItems } from "./ShoppingPanel";

describe("splitItems", () => {
  it("splits on commas, new lines and 'and'", () => {
    expect(splitItems("milk, 2 usb c cable and steel bottle")).toEqual(["milk", "2 usb c cable", "steel bottle"]);
    expect(splitItems("rice\nbread;eggs")).toEqual(["rice", "bread", "eggs"]);
  });
  it("drops blanks and caps at ten", () => {
    expect(splitItems(" , ,")).toEqual([]);
    expect(splitItems(Array.from({ length: 14 }, (_, i) => `item${i}`).join(","))).toHaveLength(10);
  });
});
