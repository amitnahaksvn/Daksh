# Discord

Discord connects with **a bot the person creates themselves**: they paste the bot token in Apps → Discord. Nothing is configured on the
server. The token is checked against Discord, stored encrypted and never shown again. Daksha reads and posts only in servers the bot was invited to.
(Discord's own OAuth cannot read messages, which is why a bot is used.)

Reading is on by default. **Full access** is Daksha's own switch for posting as the bot.

## Each person, once (discord.com/developers/applications → New Application)
1. **Bot** page: turn on **Message Content Intent** (otherwise message text arrives empty), then **Reset Token** and copy it.
2. Paste the token in Daksha. Daksha then offers an **Invite the bot to a server** link (permissions: View Channels, Read Message History, Send Messages).
3. In Discord turn on Developer Mode to copy channel IDs for the dashboard's "Channels to watch" setting.

## What it feeds
- **/apps/discord:** the bot's servers, their text and announcement channels, messages with older pages, and (full access) posting.
- **Dashboard tile:** latest messages in up to five watched channels (or the server list). **Assistant:** latest messages in watched channels.
- No live feed: Discord only pushes messages over a gateway connection, which Daksha does not hold.

API: `/api/integrations/discord/…` `status connect disconnect write settings invite guilds guilds/{id}/channels channels/{id}/messages dashboard`.
