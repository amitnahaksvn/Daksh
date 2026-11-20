import type { Metadata } from "next";
import { LegalPage, LEGAL_CONTACT } from "@/components/LegalPage";

export const metadata: Metadata = { title: "Privacy Policy — Daksha" };

export default function PrivacyPage() {
  return (
    <LegalPage title="Privacy Policy">
      <p>Daksha (&ldquo;we&rdquo;, &ldquo;the app&rdquo;) is a personal AI operator that helps you manage conversations, tasks, reminders, email and calendar. This policy explains what data we handle and why.</p>

      <h2>Information we collect</h2>
      <ul>
        <li>Account details you provide: name, email address and password (stored hashed).</li>
        <li>Content you create in the app: chats, tasks, reminders and settings.</li>
        <li>Data from services you explicitly connect, such as Gmail, WhatsApp or Telegram, only after you grant access.</li>
      </ul>

      <h2>Google user data (Gmail)</h2>
      <p>If you connect Gmail, we request these scopes:</p>
      <ul>
        <li><strong>gmail.readonly</strong> — to list and display your messages inside Daksha.</li>
        <li><strong>gmail.send</strong> — to send an email only when you ask us to.</li>
        <li><strong>calendar.events</strong> — to show your Google Calendar events and add an event only when you ask us to.</li>
      </ul>
      <p>We use this data solely to provide these user-facing features. We do not sell it, use it for advertising, or transfer it to third parties except as needed to operate the feature or to comply with law. We do not allow humans to read your Gmail data unless you give consent, it is necessary for security or abuse investigation, or it is required by law. Authorization tokens are encrypted at rest.</p>
      <p>Daksha&rsquo;s use and transfer of information received from Google APIs adheres to the <a className="underline" href="https://developers.google.com/terms/api-services-user-data-policy#limited-use">Google API Services User Data Policy</a>, including the Limited Use requirements.</p>

      <h2>AI processing</h2>
      <p>Content you send to the assistant may be processed by the AI provider configured for the app to generate replies. Long-term memory is opt-in.</p>

      <h2>Retention and deletion</h2>
      <p>You can disconnect Gmail at any time from the Apps page, which deletes the stored tokens. You can also revoke access at <a className="underline" href="https://myaccount.google.com/permissions">myaccount.google.com/permissions</a>. To delete your account and data, email us at the address below.</p>

      <h2>Security</h2>
      <p>Data is transmitted over HTTPS, secrets are encrypted at rest, and sensitive actions are audited.</p>

      <h2>Contact</h2>
      <p>Questions or deletion requests: <a className="underline" href={`mailto:${LEGAL_CONTACT}`}>{LEGAL_CONTACT}</a>.</p>
    </LegalPage>
  );
}
