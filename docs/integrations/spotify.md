# Spotify

Spotify connects with **Authorization Code + PKCE** (no client secret required). One `spotify` connection per person; the encrypted
refresh token is used to get a new one-hour access token automatically.

Read-only by default: now playing, recent plays, top tracks, playlists and search. **Full access** adds playback control, queueing and
playing a track or playlist (`user-modify-playback-state`, `playlist-modify-*`). Spotify only allows playback control on **Premium**.

## One-time setup (developer.spotify.com/dashboard → Create app)
- **Redirect URI:** `https://<API_HOST>/api/integrations/spotify/callback`. For local development Spotify does not accept `localhost`
  over http, so use the loopback address: `http://127.0.0.1:5080/api/integrations/spotify/callback`.
- APIs used: Web API. While the app is in *Development mode* only users added under **User Management** (up to 25) can sign in; a public
  launch needs Spotify's extended quota request.

```
Spotify__OAuth__ClientId=
Spotify__OAuth__RedirectUri=https://<API_HOST>/api/integrations/spotify/callback
Spotify__OAuth__FrontendUrl=https://<WEB_ORIGIN>/apps/spotify
# Spotify__OAuth__ClientSecret=   # optional; PKCE does not need it
```

## What it feeds
- **/apps/spotify:** a player (refreshes every 10 s, with controls on full access), recently played, top tracks, playlists, search.
- **Dashboard tile:** now playing and recent plays. **Assistant:** what is playing and what was played recently.
- Spotify has no token-revoke API: Disconnect makes Daksha forget the token; remove access at spotify.com/account/apps to cut it fully.

API: `/api/integrations/spotify/…` `status connect callback disconnect write settings now control play queue recent top playlists playlists/{id}/tracks search dashboard`.
