# Deploying SmartMeetUp

Single-VM demo deployment: one Docker Compose stack behind Caddy, which obtains and renews TLS
certificates automatically. Everything except the AI, transcription and email SaaS runs on the box.

Nothing in this stack is Oracle-specific — any Ubuntu 22.04 VM with a public IP works (Hetzner CX22,
Contabo, a spare box). Oracle's Always Free A1 tier is just the cheapest option that is large enough.

---

## What runs where

| Service | Reachable at | Notes |
|---|---|---|
| `caddy` | `:80`, `:443` | TLS termination, single entry point |
| `web` | `https://$DOMAIN/` | nginx serving the Angular build |
| `api` | `https://$DOMAIN/api/*` | .NET API + Hangfire worker in-process |
| `livekit` | `wss://$DOMAIN/livekit` + UDP `7881/7882`, TCP `5349` | media bypasses Caddy — see below |
| `livekit-egress` | internal | records meetings, uploads to MinIO |
| `minio` | `https://$DOMAIN/storage/*` | recordings; objects stay private |
| `postgres` | internal only | not published to the host |
| `redis` | internal only | LiveKit coordination |
| `seq` | `https://$DOMAIN/logs/` | structured logs, own admin login |

**Why the LiveKit ports are published directly:** WebRTC media is UDP and cannot pass through an
HTTPS reverse proxy. Only the signalling WebSocket goes through Caddy. TCP 5349 is TURN, needed by
viewers on networks that block UDP entirely.

---

## First deploy

### 1. Provision the VM

Create an Ubuntu 22.04 VM with a public IP. For Oracle's Always Free tier: `VM.Standard.A1.Flex`,
4 OCPU / 24 GB, 100 GB boot volume.

> `Out of capacity` on A1 shapes is common in busy regions. Either retry on a loop, try another
> region, or use any other provider — nothing here depends on Oracle.

Then, **in the Oracle Cloud console**, add ingress rules to the VCN security list. This is a separate
firewall from the VM's own, and traffic never arrives without it:

| Port | Protocol | Purpose |
|---|---|---|
| 22 | TCP | SSH — restrict to your own IP |
| 80 | TCP | HTTP; required for certificate issuance and renewal |
| 443 | TCP + UDP | HTTPS and HTTP/3 |
| 7881, 7882 | UDP | LiveKit media |
| 5349 | TCP | LiveKit TURN |

### 2. Bootstrap the machine

```bash
sudo bash deploy/scripts/provision.sh
```

Installs Docker and the Compose plugin, configures `ufw`, creates `/opt/smartmeetup`, and installs
the nightly database backup cron job. Log out and back in afterwards so docker group membership
applies.

### 3. Clone and configure

```bash
git clone <your-repo> /opt/smartmeetup
cd /opt/smartmeetup

cp deploy/.env.example deploy/.env
chmod 600 deploy/.env
nano deploy/.env
```

`deploy/.env` is gitignored and must never be committed. Generate each secret with:

```bash
openssl rand -base64 36
```

Only these are strictly required for the app to run: `DOMAIN`, `JWT_KEY`, `POSTGRES_PASSWORD`,
`LIVEKIT_API_KEY`, `LIVEKIT_API_SECRET`, `MINIO_ROOT_USER`, `MINIO_ROOT_PASSWORD`. Everything else is
optional and degrades cleanly — see *Optional keys* below.

### 4. Point DNS at the VM

For `smartmeetup.is-a.dev`: fork [`is-a-dev/register`](https://github.com/is-a-dev/register), add
`domains/smartmeetup.json`:

```json
{
  "owner": { "username": "<your-gh-user>", "email": "<you>@example.com" },
  "record": { "A": ["<vm-public-ip>"] }
}
```

Open a PR and wait for the merge.

**Don't want to wait?** Set `DOMAIN=<vm-public-ip>.nip.io`. `nip.io` resolves any
`<ip>.nip.io` to that IP, and Caddy will still issue a real certificate for it — so the full
deployment can be demonstrated before the PR lands. Switch `DOMAIN` later and restart.

### 5. Bring it up

```bash
docker compose -f deploy/docker-compose.prod.yml up -d --build
```

First run takes a few minutes: it builds both images and Caddy fetches a certificate. Database
migrations apply automatically on API startup.

### 6. Verify

```bash
curl https://$DOMAIN/health/ready          # {"status":"Healthy"}
curl -I https://$DOMAIN/                   # 200, and a valid certificate
docker compose -f deploy/docker-compose.prod.yml ps   # every service up
```

Then in a browser: sign up, start a meeting from a second browser, confirm audio and video, and check
that `● REC` appears. End the meeting; if the transcription key is set, the transcript and analysis
appear within a few minutes.

To reach `/hangfire`, set `ADMIN_EMAIL` to an account that has already signed up and restart the API —
the Admin role is granted at startup.

---

## Optional keys and what happens without them

The app is built to degrade rather than break. Leave a key blank and only that feature stops.

| Key | Without it |
|---|---|
| `ASSEMBLYAI_API_KEY` | Meetings record but are never transcribed. Analysis, speaker attribution and search all read the transcript, so none of them run either. |
| `GEMINI_API_KEY` (or Groq / OpenRouter / OpenAI) | No summaries, action items, decisions or email drafts. Meetings finish at `Ready`. Search still works in keyword mode — PostgreSQL builds that index itself. |
| `GEMINI_API_KEY` specifically | Also disables semantic search. The keyword arm continues; hybrid quietly falls back to it. |
| `RESEND_API_KEY` | Follow-up emails are drafted but sending refuses with a clear message. |

Gemini gives the most for one key: it is the default analysis provider **and** powers search
embeddings. Free at [aistudio.google.com/apikey](https://aistudio.google.com/apikey).

---

## Day-to-day operations

**Redeploy** — under a minute; migrations run on startup:

```bash
cd /opt/smartmeetup && git pull
docker compose -f deploy/docker-compose.prod.yml up -d --build
```

**Logs:**

```bash
docker compose -f deploy/docker-compose.prod.yml logs -f api
```

Or browse structured logs at `https://$DOMAIN/logs/`.

**Database backups** — nightly at 03:15 UTC via cron, 7 days retained in `/opt/smartmeetup/backups`.
Run one by hand with `bash deploy/scripts/backup-db.sh`. Restore:

```bash
gunzip -c backups/<file>.sql.gz | docker exec -i smartmeetup-postgres psql -U postgres -d smartmeetupdb
```

> These backups live on the same disk as the database, which is fine for a demo but is not a real
> backup strategy. Copy them off the box if the data starts to matter.

**Recording retention** — the API applies a 30-day expiry rule to the bucket at startup, enforced by
MinIO itself. Change it with `BlobStorage__RetentionDays`.

**Uptime monitoring** — point UptimeRobot (or similar) at `https://$DOMAIN/health/ready` every
5 minutes.

---

## Rate limits in production

Enforced by the API, partitioned per user where a user exists and per IP otherwise:

| Scope | Limit |
|---|---|
| Auth endpoints | 10 / minute / IP |
| General API | 100 / minute / user |
| AI, email and re-index triggers | 5 / minute / user |
| Meeting creation | **10 / day / user** |

The daily meeting cap is the one that matters most: a meeting is the entry point to recording,
transcription and analysis, so it is what actually protects the free-tier quotas from being burned
through. Tune any of them under `Security__RateLimits__*`.

---

## Deliberately not included

Scoped out for a single-VM demo, and each is a small change when it stops being true:

- **CI/CD** — deploys are `git pull` + `up -d --build`.
- **Redis-backed presence / SignalR backplane** — one API instance, so in-memory is correct. Needed
  only when scaling to more than one.
- **A CDN** — Caddy compresses and the static bundle is small.
- **Off-box backups, alerting, tracing** — Seq covers a demo's diagnostics.

---

## Troubleshooting

**No certificate issued.** Port 80 must be reachable from the internet — check both `ufw` and the
VCN security list. Confirm DNS resolves to the VM: `dig +short $DOMAIN`. Then `docker compose logs caddy`.

**Calls connect but no audio or video.** Almost always UDP 7881/7882 blocked in the VCN security
list. Signalling goes through Caddy so the call *appears* to start, then no media arrives.

**Recordings never appear.** Check `docker compose logs livekit-egress`. The egress worker needs
`CAP_SYS_ADMIN` (already set) and must reach MinIO on the internal network.

**Transcript stays at `Processing`.** AssemblyAI fetches the recording over a presigned URL, so
`https://$DOMAIN/storage/*` must be publicly reachable. Verify with
`curl -I "<presigned-url-from-logs>"`.
