import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { ErrorState } from "./ui";

describe("ErrorState", () => {
  it("announces the error and offers retry", async () => {
    const retry = vi.fn();
    render(<ErrorState error={new Error("boom")} onRetry={retry} />);
    expect(screen.getByRole("alert")).toHaveTextContent("boom");
    await userEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(retry).toHaveBeenCalled();
  });
});
