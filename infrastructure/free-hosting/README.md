# Free QA hosting

| Piece | Where | Cost |
|-------|-------|------|
| Web (static export) | Azure Static Web Apps, **Free** plan (`daksh-web-qa` in `rg-daksh-qa`) | $0 |
| api, whatsapp, telegram | one free VM (Oracle Always Free recommended) running `docker-compose.yml` + Caddy | $0 |
| Database | MongoDB Atlas M0 | $0 |

## One-time setup
1. Create the VM, open ports 80/443, install Docker. Note its public IP.
2. Copy `.env.example` to `~/daksh/.env` on the VM and fill it in. Free hostnames: `api.1-2-3-4.sslip.io` for IP 1.2.3.4.
3. GitHub repo secrets: `VM_HOST`, `VM_USER`, `VM_SSH_KEY`, `AZURE_STATIC_WEB_APPS_API_TOKEN`
   (token: `az staticwebapp secrets list -n daksh-web-qa -g rg-daksh-qa --query properties.apiKey -o tsv`).
   Repo variable: `API_HOST` (the API hostname, no scheme).
4. Run the "Deploy services to free VM" and "Deploy web (static)" workflows.

## Monitor URLs
- API: `https://<API_HOST>/health/live`
- WhatsApp: `https://<WHATSAPP_HOST>/health`
- Telegram: `https://<TELEGRAM_HOST>/health`
- Web: the Static Web Apps URL

Add `https://<API_HOST>`'s origin to Cors (done via `WEB_ORIGIN` in `.env`). Never create billable Azure resources for Daksha.
