import type { Metadata } from "next";
import { LegalPage, LEGAL_CONTACT } from "@/components/LegalPage";

export const metadata: Metadata = { title: "Terms of Service — Daksha" };

export default function TermsPage() {
  return (
    <LegalPage title="Terms of Service">
      <p>By creating an account or using Daksha you agree to these terms.</p>

      <h2>The service</h2>
      <p>Daksha is a personal AI operator for conversations, tasks, reminders, email and calendar. Features may change, and the service is provided &ldquo;as is&rdquo; without warranties of any kind.</p>

      <h2>Your account</h2>
      <p>You are responsible for your account credentials and for activity under your account. You must provide accurate information and be legally able to use the service.</p>

      <h2>Connected services</h2>
      <p>You choose which third-party accounts (for example Gmail) to connect and can disconnect them at any time. You are responsible for complying with those providers&rsquo; terms. Actions such as sending email are performed on your instruction.</p>

      <h2>Acceptable use</h2>
      <ul>
        <li>Do not use Daksha for spam, abuse, or anything unlawful.</li>
        <li>Do not attempt to disrupt, reverse engineer, or gain unauthorized access to the service.</li>
      </ul>

      <h2>AI output</h2>
      <p>AI-generated content may be inaccurate. Review it before relying on it or sending it to others.</p>

      <h2>Liability</h2>
      <p>To the extent permitted by law, we are not liable for indirect or consequential damages arising from use of the service.</p>

      <h2>Termination and changes</h2>
      <p>You may stop using Daksha at any time. We may suspend accounts that violate these terms and may update these terms; continued use means acceptance.</p>

      <h2>Contact</h2>
      <p><a className="underline" href={`mailto:${LEGAL_CONTACT}`}>{LEGAL_CONTACT}</a></p>
    </LegalPage>
  );
}
