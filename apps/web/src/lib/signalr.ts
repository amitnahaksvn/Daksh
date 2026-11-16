import * as signalR from "@microsoft/signalr";
import { API_URL } from "@/lib/api";
import { getSession } from "@/lib/session";

/** One connection to the integrations hub, reused across pages while the user is signed in. */
let connection: signalR.HubConnection | null = null;

export function getIntegrationsHub(): signalR.HubConnection {
  if (connection) return connection;
  connection = new signalR.HubConnectionBuilder()
    .withUrl(`${API_URL}/hubs/integrations`, { accessTokenFactory: () => getSession()?.accessToken ?? "" })
    .withAutomaticReconnect()
    .build();
  return connection;
}
