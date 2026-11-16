import { setAccessToken, type AccountProfile } from "@/lib/api";

const KEY = "daksh.session";

/** The signed-in user's profile, persisted so a page refresh doesn't lose the session. Includes the
 *  real access token the API issues on login/verify-otp — see rehydrateAccessToken() below. */
export function getSession(): AccountProfile | null {
  try {
    const raw = localStorage.getItem(KEY);
    return raw ? (JSON.parse(raw) as AccountProfile) : null;
  } catch {
    return null;
  }
}

export function setSession(profile: AccountProfile) {
  try {
    localStorage.setItem(KEY, JSON.stringify(profile));
  } catch {
    /* ignore (private browsing, storage disabled, etc.) */
  }
  setAccessToken(profile.accessToken);
}

export function clearSession() {
  try {
    localStorage.removeItem(KEY);
  } catch {
    /* ignore */
  }
  setAccessToken(null);
}

/**
 * `setAccessToken` in lib/api.ts is an in-memory variable, so it's lost on every full page reload.
 * Call this once on mount wherever an authenticated page needs it (RequireAuth does this for the
 * whole app shell) to restore it from the persisted session before any API call fires.
 */
export function rehydrateAccessToken(): AccountProfile | null {
  const session = getSession();
  if (session?.accessToken) setAccessToken(session.accessToken);
  return session;
}
