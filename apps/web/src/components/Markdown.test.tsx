import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { Markdown } from "./Markdown";

describe("Markdown", () => {
  it("shows * bullets as a list, **bold** as bold, and keeps links", () => {
    const { container } = render(<Markdown text={"According to your chats:\n* **Choti (WhatsApp):** Last message on 20 Nov: \"Ok\"\n* **PSW**: see https://example.com/a\n\nDone."} />);
    expect(screen.getAllByRole("listitem")).toHaveLength(2);
    expect(container.querySelectorAll("strong")).toHaveLength(2);
    expect(container.textContent).not.toContain("**");
    expect(screen.getByRole("link").getAttribute("href")).toBe("https://example.com/a");
    expect(screen.getByText("Done.")).toBeTruthy();
  });
  it("numbers ordered lists and leaves a half-typed ** alone", () => {
    const { container } = render(<Markdown text={"1. First\n2. Second\nSo far **unfin"} />);
    expect(container.querySelectorAll("ol > li")).toHaveLength(2);
    expect(container.textContent).toContain("**unfin");
  });
});
