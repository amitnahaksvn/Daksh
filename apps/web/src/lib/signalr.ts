import * as signalR from "@microsoft/signalr";
import { API_URL } from "@/lib/api";
import { getSession } from "@/lib/session";

/** One connection to the integrations hub, reused across pages while the user is signed in. */
let connection: signalR.HubConnection | null = null;

export function getIntegrationsHub(): signalR.HubConnection {
  if (connection) return connection;
  connection = new signalR.HubConnectionBuilder()
    .withUrl(`${API_URL}/hubs/integrations`, {
      accessTokenFactory: () => getSession()?.accessToken ?? "",
      // Auth is a bearer token, not cookies. The default (withCredentials: true) makes the browser demand
      // Access-Control-Allow-Credentials, which the API's CORS policy doesn't send, so negotiate fails.
      withCredentials: false,
    })
    // Default withAutomaticReconnect() gives up for good after ~4 attempts (0/2s/10s/30s) and never retries
    // again — fine for a network blip, not for the API being down longer than that (a deploy, a crash
    // restart). Retry forever instead, capped at 30s between attempts, so a live page recovers on its own
    // once the server comes back rather than silently going stale until the user reloads.
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: (ctx) => Math.min(1000 * 2 ** ctx.previousRetryCount, 30_000),
    })
    .build();
  // If every reconnect attempt is ever exhausted (shouldn't happen with the policy above, but belt-and-
  // braces), fall back to a plain restart loop instead of leaving the connection dead until a page reload.
  connection.onclose(() => {
    if (connection?.state === signalR.HubConnectionState.Disconnected) {
      setTimeout(() => connection?.start().catch(() => void 0), 5000);
    }
  });
  return connection;
}
