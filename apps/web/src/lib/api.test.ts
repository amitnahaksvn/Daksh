import { afterEach, describe, expect, it, vi } from "vitest";
import { api, ApiError } from "./api";

afterEach(() => vi.unstubAllGlobals());

function stubFetch(status: number, body: unknown) {
  const fn = vi.fn().mockResolvedValue(new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } }));
  vi.stubGlobal("fetch", fn);
  return fn;
}

describe("api client", () => {
  it("posts messages to the assistant endpoint", async () => {
    const fetchFn = stubFetch(200, { reply: "hi" });
    await api.sendMessage("hello");
    const [url, init] = fetchFn.mock.calls[0];
    expect(url).toContain("/api/v1/assistant/messages");
    expect(JSON.parse(init.body)).toEqual({ message: "hello" });
  });

  it("maps validation problems to ApiError with field errors", async () => {
    stubFetch(400, { title: "Validation failed", errors: { Message: ["must not be empty"] } });
    await expect(api.sendMessage("")).rejects.toMatchObject({ status: 400, fieldErrors: { Message: ["must not be empty"] } });
  });

  it("reports unauthenticated requests clearly", async () => {
    stubFetch(401, {});
    await expect(api.tasks()).rejects.toThrow(/sign in/i);
  });

  it("reports network failure", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("fail")));
    const err = await api.tasks().catch((e) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(err.status).toBe(0);
  });
});
