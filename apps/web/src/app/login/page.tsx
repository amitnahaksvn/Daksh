import Link from "next/link";
import { Notice } from "@/components/ui";

export default function LoginPage() {
  return (
    <main className="mx-auto w-full max-w-md px-4 py-16">
      <h1 className="text-2xl font-semibold">Sign in</h1>
      <Notice>
        Identity provider integration is not configured yet (requirement AUTH-001). The API accepts OIDC bearer
        tokens when <code>Auth__Authority</code> is set; for local development use <code>NEXT_PUBLIC_DEV_AUTH=true</code>.
      </Notice>
      <Link href="/dashboard" className="text-indigo-700 underline">Continue to dashboard (development)</Link>
    </main>
  );
}
