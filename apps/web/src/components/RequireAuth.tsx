"use client";
import { useRouter } from "next/navigation";
import { useEffect, useSyncExternalStore } from "react";
import { rehydrateAccessToken } from "@/lib/session";

function subscribe() {
  // No cross-tab live updates needed yet; just satisfies useSyncExternalStore's contract.
  return () => {};
}

/** Gates its children behind a signed-in session; sends anonymous visitors to /login. Also restores the
 *  in-memory API access token from the persisted session, since it doesn't survive a page reload on its own. */
export function RequireAuth({ children }: { children: React.ReactNode }) {
  const router = useRouter();
  // getServerSnapshot returns null (unknown) since localStorage isn't available during SSR.
  const authed = useSyncExternalStore(subscribe, () => rehydrateAccessToken() !== null, () => null);

  useEffect(() => {
    if (authed === false) router.replace("/login");
  }, [authed, router]);

  if (!authed) return null;
  return <>{children}</>;
}
