import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "Daksh — your personal AI operator",
  description: "Daksh is a personal AI operator for conversations, tasks, reminders, email and calendar, under your control.",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" className="h-full antialiased">
      <body className="min-h-full flex flex-col">{children}</body>
    </html>
  );
}
