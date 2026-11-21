# Free QA hosting (all $0)

| Piece | Where | Account |
|-------|-------|---------|
| Web (static export) | Azure Static Web Apps **Free**, `daksh-web-qa` (rg-daksh-qa) | Azure `amit@webappslab.com` |
| API (+ Gmail sync, reminders) | Oracle Always Free micro VM `daksh-qa-api` (140.245.242.128) | Oracle (Hyderabad) |
| WhatsApp + Telegram bridges | Oracle Always Free micro VM `daksh-qa-bridges` (140.245.252.254) | Oracle (Hyderabad) |
| Database | MongoDB Atlas M0 | existing |

Never create billable resources for Daksha. Both VMs are `VM.Standard.E2.1.Micro` (Always Free); stay within 2 of them + one 200 GB block-storage total.

## URLs
- Web: https://proud-field-07d6c6b00.8.azurestaticapps.net
- API health: https://api.140-245-242-128.sslip.io/health/live
- WhatsApp health: https://whatsapp.140-245-252-254.sslip.io/health
- Telegram health: https://telegram.140-245-252-254.sslip.io/health

## How deploys work
- `deploy-web.yml`: static export -> Azure Static Web Apps (secret `AZURE_STATIC_WEB_APPS_API_TOKEN`, var `API_HOST`).
- `deploy-vm.yml`: builds amd64 images -> GHCR -> SSH to both VMs (`VM_USER`, `VM_SSH_KEY`, `VM_API_HOST`, `VM_BRIDGES_HOST`) and runs `docker compose pull && up -d`.
- App secrets live only in `~/daksh/.env` on each VM (see `api/.env.example`, `bridges/.env.example`). SSH access: user `ubuntu`.
- Gmail OAuth redirect URI to authorize in Google: `https://api.140-245-242-128.sslip.io/api/integrations/gmail/callback`.
