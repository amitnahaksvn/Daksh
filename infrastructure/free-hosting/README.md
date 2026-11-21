# Free QA hosting (all $0)

| Piece | Azure resource (rg-daksh-qa) | Plan |
|-------|------------------------------|------|
| Web (static export) | `daksh-web-qa` | Static Web Apps **Free** |
| API (+ Gmail sync) | `daksh-api-qa` | App Service **F1** (`asp-daksh-qa-free`) |
| WhatsApp bridge | `daksh-whatsapp-qa` | same F1 plan |
| Telegram bridge | `daksh-telegram-qa` | same F1 plan |
| Database | Atlas | M0 |

F1 cannot be billed: each app gets ~60 CPU-min/day and is stopped (not charged) if it goes over. Do not create other paid Azure resources for Daksha.

## Health endpoints (point the uptime monitor at these, every 5 min)
- API: https://daksh-api-qa.azurewebsites.net/health/live
- WhatsApp: https://daksh-whatsapp-qa.azurewebsites.net/health
- Telegram: https://daksh-telegram-qa.azurewebsites.net/health
- Web: https://proud-field-07d6c6b00.8.azurestaticapps.net/

## GitHub setup
Secrets: `AZURE_STATIC_WEB_APPS_API_TOKEN`, `AZURE_PUBLISH_PROFILE_API`, `AZURE_PUBLISH_PROFILE_WHATSAPP`, `AZURE_PUBLISH_PROFILE_TELEGRAM`. Variable: `API_HOST=daksh-api-qa.azurewebsites.net`.
```
az staticwebapp secrets list -n daksh-web-qa -g rg-daksh-qa --query properties.apiKey -o tsv
az webapp deployment list-publishing-profiles -n daksh-api-qa -g rg-daksh-qa --xml
```
(same for daksh-whatsapp-qa / daksh-telegram-qa)

## App settings still to add (secrets; `az webapp config appsettings set`)
- **api:** `Mongo__ConnectionString`, `Security__Encryption__Keys__k1`, `Auth__Jwt__SigningKey`, `Integrations__Whatsapp__InternalServiceKey`, `Integrations__Telegram__InternalServiceKey`, `Integrations__Telegram__ApiId`, `Integrations__Telegram__ApiHash`, `Gmail__OAuth__ClientId`, `Gmail__OAuth__ClientSecret` (+ `Gmail__PubSub__*` if used, any AI key).
- **whatsapp / telegram:** `MONGO_CONNECTION_STRING`, `INTERNAL_SERVICE_KEY`, `ENCRYPTION_KEYS_JSON`; telegram also `TELEGRAM_API_ID`, `TELEGRAM_API_HASH`.

## Gmail
Add `https://daksh-api-qa.azurewebsites.net/api/integrations/gmail/callback` as an authorized redirect URI on the Google OAuth client.
