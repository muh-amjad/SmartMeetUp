# Moving SmartMeetUp to Another Machine

Checklist for setting up local development on a new machine (Windows/Mac/Linux). This covers
**local dev only**. For deploying to a server/VM instead, use `deploy/README.md` and
`deploy/WALKTHROUGH.md` — don't duplicate that here.

---

## 0. Read this first: the project folder is split in two

On this machine the project lives across **two levels**:

```
D:\SmartMeetUp\                    <- NOT a git repo, files here are untracked
    docker-compose.yml             <- local dev infra stack (Postgres, Redis, LiveKit, MinIO, Seq, Mailpit)
    livekit.yaml
    livekit-egress.yaml
    PHASES.md
    PRODUCT_PLAN.md
    PHASE_0_COMPLETE.md
    PHASE_10_COMPLETE.md
    SmartMeetUp\                   <- the actual git repo (origin: github.com/muh-amjad/SmartMeetUp)
        MeetUpApi\
        MeetUpUI\
        deploy\
        README.md
        run-dev.ps1
```

**`git clone` only gets you the inner `SmartMeetUp\` folder.** The outer files
(`docker-compose.yml`, `livekit.yaml`, `livekit-egress.yaml`, and the `PHASE*`/`PRODUCT_PLAN.md`
docs) are not tracked by git anywhere and will **not** come across automatically. You must copy
them by hand (zip them up, use a USB drive/cloud folder, etc.) or recreate them on the new
machine.

If you'd rather not deal with this split going forward, consider moving those files into the
`SmartMeetUp\` repo and committing them — that's a one-time decision, not part of this checklist.

---

## 1. Prerequisites on the new machine

Install these before touching the project:

- **Git**
- **.NET 8 SDK**
- **Node.js 20+** (and npm, bundled with it)
- **Docker Desktop** (with WSL2 backend if Windows) — runs Postgres, Redis, LiveKit, MinIO, Seq,
  Mailpit, and the LiveKit egress worker
- A code editor (VS / VS Code / Rider — whatever you used before)

No SQL Server is needed despite what the repo's `README.md` says under Prerequisites — that line
is stale; the project actually runs on **PostgreSQL** (via Docker), confirmed by
`MeetUp.Api.csproj` (Npgsql) and `appsettings.Development.json`.

---

## 2. Get the code

```powershell
git clone https://github.com/muh-amjad/SmartMeetUp.git SmartMeetUp
cd SmartMeetUp
git branch -a          # confirm you're on `main`; there's also a `meetup-extension-for-ai` branch
```

Before leaving the old machine, double check nothing is left uncommitted:

```powershell
git status
```

(It was clean as of writing this doc — verify again at the time you actually migrate.)

Then copy the outer-level files described in section 0 into the parent folder of the cloned repo,
so the layout matches what's shown there.

---

## 3. Install dependencies

```powershell
cd MeetUpUI
npm install

cd ..\MeetUpApi\MeetUp.Api
dotnet restore
```

---

## 4. Start the local infra stack (Docker)

From the **outer** folder (where `docker-compose.yml` lives, one level above the git repo):

```powershell
docker compose up -d
```

This starts:

| Service | Port(s) | Purpose |
|---|---|---|
| postgres (pgvector) | 5433 (mapped from 5432) | main database |
| pgadmin | 5050 | Postgres admin UI |
| seq | 5340 (UI), 5341 | structured logs |
| mailpit | 1025 (SMTP), 8025 (UI) | catches local outgoing email |
| redis | 6379 | LiveKit coordination |
| livekit | 7880 (WS), 7881/7882 (UDP) | video/audio signaling + media |
| minio | 9000 (S3 API), 9001 (console) | recordings storage |
| livekit-egress | internal | records meetings, uploads to MinIO |

Make sure none of these ports are already in use on the new machine before starting.

Verify everything is healthy:

```powershell
docker compose ps
```

---

## 5. Apply database migrations

```powershell
cd MeetUpApi\MeetUp.Api
dotnet ef database update
```

This creates the schema against the Postgres container from step 4. Existing migrations already
in the repo cover auth, meetings/chat, transcripts, AI analysis, speaking analytics, search, and
follow-up email sending — you don't need to create new ones, just apply them.

> Your actual **data** (users, meetings, recordings you created on the old machine) stays in the
> old machine's Docker volumes and does **not** transfer automatically. If you need it, dump it on
> the old machine first:
> ```bash
> docker exec smartmeetup-postgres pg_dump -U postgres smartmeetupdb > smartmeetupdb.sql
> ```
> and restore on the new one:
> ```bash
> docker exec -i smartmeetup-postgres psql -U postgres -d smartmeetupdb < smartmeetupdb.sql
> ```
> Skip this if a fresh local database is fine.

---

## 6. Configuration — what needs changing, what doesn't

`MeetUpApi/MeetUp.Api/appsettings.Development.json` is committed to git with working **local dev
defaults** (Postgres on `localhost:5433`, LiveKit `devkey`/`devsecret...`, MinIO
`minioadmin`/`minioadmin123`, Mailpit on `localhost:1025`). These match the outer
`docker-compose.yml` exactly, so **nothing needs to change** for the core app (auth, meetings,
calls, recording) to work out of the box.

Optional third-party keys are blank by default and only needed if you want those specific features:

| Key (in `appsettings.Development.json`) | Feature it enables |
|---|---|
| `AssemblyAi:ApiKey` | Meeting transcription |
| `AiProviders:Providers:*:ApiKey` (Gemini/OpenAI/Groq/OpenRouter) | Summaries, action items, semantic search |
| `Email:Resend:ApiKey` | Actually sending follow-up emails (Mailpit catches them locally either way) |

If you were using any of these on the old machine, fetch the key values from wherever you store
them (password manager, etc.) — **do not** copy them via git, chat, or any shared/unencrypted
channel — and paste them into the corresponding fields on the new machine.

`MeetUpUI/src/environments/environment.development.ts` already points at
`http://localhost:5131`, which matches the API's default (http) launch profile — no changes
needed there either.

---

## 7. Run the app

```powershell
cd SmartMeetUp     # repo root, where run-dev.ps1 lives
.\run-dev.ps1
```

This opens two terminals: the API (`dotnet watch run`, http://localhost:5131, Swagger at
`/swagger`) and the Angular UI (`npm start`, http://localhost:4200).

`run-dev.ps1` is Windows/PowerShell-specific. If the "other system" is Mac/Linux, run the two
commands manually in separate terminals instead:

```bash
cd MeetUpApi/MeetUp.Api && dotnet watch run
cd MeetUpUI && npm start
```

---

## 8. Verify it works

- http://localhost:4200 loads the Angular app
- Sign up a test account, log in
- Start a meeting, confirm audio/video connect (LiveKit)
- Check Swagger at http://localhost:5131/swagger
- Check logs at http://localhost:5340 (Seq)
- Check caught test emails at http://localhost:8025 (Mailpit)
- Run the backend test suite:
  ```powershell
  cd MeetUpApi
  dotnet test
  ```

---

## 9. Git identity / auth on the new machine

If this is a fresh machine, also set up:

```powershell
git config --global user.name "Your Name"
git config --global user.email "amjad.ashfaq@a-w.com"
```

and authenticate to GitHub (SSH key or a credential manager / PAT) so you can push to
`github.com/muh-amjad/SmartMeetUp`.

---

## Not covered here

- Deploying to a server/VM — see `deploy/README.md` (reference) and `deploy/WALKTHROUGH.md`
  (step-by-step, Oracle Cloud walkthrough).
- Production secrets (`deploy/.env`) — unrelated to local dev, never commit this file.
