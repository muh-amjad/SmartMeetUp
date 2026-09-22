# MeetUp AI Meeting Assistant — Complete Product Plan

> This document covers what we are building, why, the full feature set, the technology decisions,
> the database schema, and how everything fits together as a product.
> For step-by-step implementation tasks, see [PHASES.md](./PHASES.md).

---

## 1. Product Vision

**MeetUp AI Meeting Assistant** is a video calling platform that goes beyond real-time communication.
It records every meeting, transcribes the audio with speaker identification, and uses AI to extract
structured insights — so teams spend less time writing notes and following up, and more time actually doing the work.

### Core value proposition

| Without MeetUp AI | With MeetUp AI |
|-------------------|----------------|
| Someone has to take manual notes | Auto-generated summary after every call |
| Action items get forgotten | Extracted, assigned, and tracked automatically |
| "What did we decide?" — nobody remembers | Every decision logged with source quote |
| Follow-up email takes 20 min to write | AI drafts it; host reviews and sends in 30 seconds |
| Can't find what was discussed 3 weeks ago | Natural-language search across all meeting history |
| No idea how meetings are going | Speaking analytics, duration trends, topic frequency |

---

## 2. Target Users

**Primary:** Small-to-mid teams (5–50 people) who hold regular video meetings —
engineering standups, product reviews, sales calls, one-on-ones.

**Secondary:** Individual professionals (consultants, freelancers, coaches)
who want a record of every client conversation.

---

## 3. What We Are Building — Full Feature List

### 3-A · Video Calling (existing + improved)
- 1-to-1 and multi-participant video calls (up to 50 participants via LiveKit SFU)
- Camera and microphone toggle during calls
- In-meeting text chat
- "Instant meeting" — start now and share a link
- Meeting invite to existing users via the app
- Pre-call media preview (camera/mic check before joining)

### 3-B · Recording
- Every meeting is automatically recorded (audio-only OGG/Opus)
- Recording stored in MinIO (S3-compatible blob storage running on the same VM)
- Playback available on the meeting detail page after the call ends
- Recording indicator visible during the call

### 3-C · Transcription (AssemblyAI)
- Full audio-to-text transcription with timestamps
- Speaker diarization — each utterance labeled by speaker
- Speaker labels resolved to real user names (e.g. "Speaker A" → "Alice")
- Language detection (supports all languages AssemblyAI supports)
- Transcript viewer UI — scrollable, click-to-jump to moment in audio

### 3-D · AI-Powered Analysis (pluggable LLM provider)
Each user picks their preferred LLM in Settings. Default is **Google Gemini 2.5 Flash** (free);
alternatives include OpenAI `gpt-4o-mini`, Groq Llama 3.3 70B, and DeepSeek V3 (via OpenRouter).
- **Meeting summary** — 2–4 paragraph overview of what was discussed
- **Key topics** — list of 5–10 topic tags extracted from the conversation
- **Action items** — every task extracted with:
  - What needs to be done
  - Who it is assigned to (matched to a real user account when possible)
  - Due date (if mentioned in the conversation)
- **Decisions** — every conclusion or commitment made during the meeting
- **Follow-up email draft** — full email body the host can review, edit, and send
- A small badge on the meeting detail page shows which model actually produced the analysis
  (helps compare providers side-by-side).

### 3-E · Action Item Tracking
- Dashboard widget showing all open action items across all meetings
- Dedicated action items page with filters: Open / Done / Overdue / Assigned to me
- Mark items as complete with a checkbox
- Edit description, change assignee, set/change due date
- Click any action item → jumps to the moment in the transcript where it was mentioned

### 3-F · Natural Language Search
- Search bar accessible from every authenticated page
- Hybrid search mode: combines keyword matching + semantic (vector) similarity
- Example: searching "pricing discussion" returns meetings where "cost", "budget", "proposal" came up
- Results scoped to meetings the user attended (no cross-account data leakage)
- Each result shows a text snippet with the relevant passage highlighted
- "Jump to moment" link → opens meeting detail and seeks audio to that timestamp

### 3-G · Meeting History & Dashboard
- Full meeting history list (paginated, sortable, filterable by status/date)
- Status badges: Scheduled / Live / Processing / Ready / Failed
- Meeting detail page with all AI outputs (summary, transcript, action items, decisions, email)
- Quick-access recent meetings on the main dashboard

### 3-H · Speaking Analytics
- Per-meeting: how much each participant spoke (seconds, percentage, bar chart)
- Per-participant: total speaking time across all meetings in a date range
- Word count per participant
- Average words per minute

### 3-I · Account Analytics
- Total meeting hours (all time and by period)
- Number of meetings held
- Average meeting duration
- Most frequently discussed topics (from AI-extracted key topics)
- Week-over-week meeting activity chart

### 3-J · Follow-up Email Sending
- Host reviews and optionally edits the AI-drafted email
- One-click send to all meeting participants
- Participants who opted out of email notifications are excluded
- Sent history recorded (who was sent the email, when)

### 3-K · Authentication & User Management
- Email + password sign-up and login (already built)
- JWT access tokens + refresh tokens (already built)
- User search by username or email (already built)
- Display name and email notification preferences (new in this plan)
- Password change from settings page

---

## 4. What Is NOT Included (explicit scope boundaries)

The following are deliberately out of scope for this version and can be built later:

- Calendar integration (Google Calendar / Outlook sync, scheduling from the app)
- Real-time live transcription during the call (we transcribe after the call ends)
- Zoom / Google Meet / Teams bot recorder (joining external meetings)
- Screen recording (audio-only recording; video recording is a one-line change later)
- Mobile apps (iOS / Android)
- Multi-language UI (app is English-only; AssemblyAI supports 100+ transcription languages)
- Organization / team management, billing, admin console, SSO
- White-label / custom branding
- Real-time in-call AI (sentiment analysis, interruption detection, live summaries)
- Public "join by link" without an account

---

## 5. Technology Stack

### Backend
| Layer | Technology | Why |
|-------|-----------|-----|
| Language / runtime | C# / .NET 8 (LTS) | Already in use; strong async support; official SDKs for all AI vendors |
| Web framework | ASP.NET Core 8 | Already in use |
| ORM | EF Core 8 + Npgsql | Already in use; swap provider to Postgres |
| Database | PostgreSQL 16 + pgvector extension | Vector search built-in (no separate Pinecone/Qdrant); full-text search with tsvector; self-hosted container |
| Real-time / presence | ASP.NET Core SignalR | Already in use; narrowed to presence + invites + chat |
| Media server | LiveKit (SFU) | Open-source; .NET SDK; built-in egress recording; scales to 50+ users; Docker-native; free self-host |
| Background jobs | Hangfire (Postgres-backed) | Visual dashboard; retries; scheduling; fits .NET DI naturally |
| Transcription | AssemblyAI (default) / Deepgram (fallback) | Best-in-class speaker diarization; 100 h free credit; auto-chapters; entity detection |
| AI analysis | **Pluggable providers** — Google Gemini 2.5 Flash (default, free), OpenAI GPT-4o mini, Groq Llama 3.3 70B, DeepSeek V3 (via OpenRouter) | User picks from Settings dropdown; each provider registered only if API key is configured; structured JSON output via schema |
| Embeddings | Google `text-embedding-004` (free) | 768 dimensions; works with pgvector cosine similarity; same-family model as default LLM |
| Blob storage | MinIO (self-hosted, S3-compatible) | Runs on same VM as everything else; zero external cost; `AWSSDK.S3` works out of the box; drop-in replacement for Cloudflare R2 |
| Email | Resend (default) / SendGrid (alternative) | Resend gives 3 000 emails/month free (100/day); simple HTTP API; SendGrid is a drop-in swap |
| Logging | Serilog + Seq | Structured JSON logs; Seq container runs on the VM for browsing |
| Validation | FluentValidation | Consistent ProblemDetails errors; clean validator classes |

### Frontend
| Layer | Technology | Why |
|-------|-----------|-----|
| Framework | Angular 21 (standalone) | Already in use; signals + NgRx already set up |
| State management | NgRx (meetings, users, call) + Angular signals | Already in use; facades pattern established |
| Real-time | @microsoft/signalr v10 | Already in use; narrowed to presence/invites/chat |
| Media (SFU) | livekit-client | Official LiveKit JS SDK; replaces raw RTCPeerConnection |
| Animations | GSAP (already present) | Landing page only |
| Testing (unit) | Vitest (already present) | Fast; ESM-native |
| Testing (e2e) | Playwright (already present) | Cross-browser; strong async assertions |

### Infrastructure (single-VM demo deployment)
| Layer | Technology | Why |
|-------|-----------|-----|
| Compute | **Oracle Cloud — Always-Free Ampere A1 VM** (4 OCPU, 24 GB RAM, 200 GB block, 10 TB egress) | Free forever; enough capacity for all services in one place; runs the existing `docker-compose.yml` verbatim |
| Orchestration | `docker compose` on Ubuntu 22.04 | Same stack as local dev; single-command deploy |
| Reverse proxy + TLS | **Caddy** (Let's Encrypt) | Zero-config HTTPS for the whole app on one domain |
| Domain | `smartmeetup.is-a.dev` (free, GitHub PR–approved) | Real-looking demo URL at no cost |
| DNS / CDN (optional) | Cloudflare proxy | Free DDoS protection; skipped in first deploy for simplicity |
| Audio storage | MinIO container on the same VM | S3-compatible; no external dependency |
| Media server hosting | LiveKit container on the same VM | Co-located with API for low-latency webhooks |
| CI/CD | Manual `git pull && docker compose up -d --build` (GitHub Actions later) | Redeploy in < 60 s; simpler for demo |
| Error monitoring | Sentry (optional) | Free tier; can be added post-launch |
| Log aggregation | **Seq container on the VM** | Serilog writes directly; browsed via `/logs/` route on the same domain |
| Uptime monitoring | UptimeRobot free plan | Pings `/health/ready` every 5 min |

---

## 6. Database Schema

### Existing tables (carried over)
```
AspNetUsers           — extended with:
                          OptOutFollowUpEmails       bool
                          PreferredAnalysisProviderKey text (nullable)
AspNetRoles
AspNetUserRoles
AspNetUserClaims
AspNetRoleClaims
AspNetUserLogins
AspNetUserTokens
RefreshTokens
```

### New tables added in this plan

```
meetings
─────────────────────────────────────────────
  id                             uuid PK
  host_user_id                   text FK → AspNetUsers
  title                          text NOT NULL DEFAULT 'Untitled Meeting'
  scheduled_start_utc            timestamptz NULL
  actual_start_utc               timestamptz NULL
  ended_utc                      timestamptz NULL
  status                         smallint NOT NULL   -- enum: 0=Scheduled, 1=Live, 2=Ended, 3=Processing, 4=Ready, 5=Failed
  livekit_room_name              text NOT NULL UNIQUE
  egress_id                      text NULL
  recording_blob_key             text NULL           -- MinIO object key (e.g. recordings/abc-123/20260620T1000Z.ogg)
  recording_blob_url             text NULL           -- pre-signed download URL (refreshed on API call)
  recording_duration_sec         integer NULL
  assembly_ai_transcript_id      text NULL
  analysis_provider_requested    text NULL           -- key the user asked for (from Meeting.Host.PreferredAnalysisProviderKey at creation)
  analysis_provider_used         text NULL           -- key that actually ran (may differ if fallback triggered)
  created_utc                    timestamptz NOT NULL DEFAULT now()
  updated_utc                    timestamptz NOT NULL DEFAULT now()

meeting_participants
─────────────────────────────────────────────
  id             uuid PK
  meeting_id     uuid FK → meetings (CASCADE DELETE)
  user_id        text FK → AspNetUsers (CASCADE DELETE)
  role           smallint NOT NULL   -- 0=Host, 1=Participant
  joined_utc     timestamptz NULL
  left_utc       timestamptz NULL
  speaking_seconds integer NOT NULL DEFAULT 0
  UNIQUE (meeting_id, user_id)

chat_messages
─────────────────────────────────────────────
  id             uuid PK
  meeting_id     uuid FK → meetings (CASCADE DELETE)
  sender_user_id text FK → AspNetUsers
  text           text NOT NULL   -- max 2000 chars enforced in app
  sent_utc       timestamptz NOT NULL DEFAULT now()
  INDEX (meeting_id, sent_utc)

transcripts
─────────────────────────────────────────────
  id                       uuid PK
  meeting_id               uuid FK → meetings (CASCADE DELETE) UNIQUE
  language                 text NOT NULL DEFAULT 'en'
  full_text                text NOT NULL DEFAULT ''
  search_vector            tsvector GENERATED ALWAYS AS (to_tsvector('english', full_text)) STORED
  assembly_ai_transcript_id text NULL
  created_utc              timestamptz NOT NULL DEFAULT now()
  GIN INDEX (search_vector)

transcript_utterances
─────────────────────────────────────────────
  id                   uuid PK
  transcript_id        uuid FK → transcripts (CASCADE DELETE)
  speaker_label        text NOT NULL      -- "A", "B", "C" (AssemblyAI raw labels)
  participant_user_id  text NULL FK → AspNetUsers   -- resolved in Phase 6
  start_ms             integer NOT NULL
  end_ms               integer NOT NULL
  text                 text NOT NULL
  confidence           real NOT NULL
  INDEX (transcript_id, start_ms)

transcript_chunks                          -- for semantic search (Phase 7)
─────────────────────────────────────────────
  id            uuid PK
  meeting_id    uuid FK → meetings
  transcript_id uuid FK → transcripts
  text          text NOT NULL              -- ~300-token window
  start_ms      integer NOT NULL
  end_ms        integer NOT NULL
  embedding     vector(768) NOT NULL       -- pgvector column (Gemini text-embedding-004)
  IVFFLAT INDEX (embedding vector_cosine_ops) WITH (lists = 100)

meeting_summaries
─────────────────────────────────────────────
  id              uuid PK
  meeting_id      uuid FK → meetings (CASCADE DELETE) UNIQUE
  overview_text   text NOT NULL
  key_topics      jsonb NOT NULL DEFAULT '[]'   -- string[]
  provider_key    text NOT NULL                 -- which analysis provider produced this (e.g. "gemini-2.5-flash")
  model_used      text NOT NULL                 -- exact model id reported by the provider
  generated_utc   timestamptz NOT NULL DEFAULT now()

action_items
─────────────────────────────────────────────
  id                    uuid PK
  meeting_id            uuid FK → meetings (CASCADE DELETE)
  description           text NOT NULL
  assignee_user_id      text NULL FK → AspNetUsers
  assignee_name_raw     text NULL         -- raw name as GPT extracted it (e.g. "Alice")
  due_date_utc          date NULL
  status                smallint NOT NULL DEFAULT 0   -- 0=Open, 1=Done, 2=Cancelled
  completed_utc         timestamptz NULL
  source_utterance_id   uuid NULL FK → transcript_utterances
  created_utc           timestamptz NOT NULL DEFAULT now()
  INDEX (meeting_id)
  INDEX (assignee_user_id, status)

decisions
─────────────────────────────────────────────
  id                  uuid PK
  meeting_id          uuid FK → meetings (CASCADE DELETE)
  description         text NOT NULL
  source_utterance_id uuid NULL FK → transcript_utterances
  created_utc         timestamptz NOT NULL DEFAULT now()

follow_up_emails
─────────────────────────────────────────────
  id               uuid PK
  meeting_id       uuid FK → meetings (CASCADE DELETE) UNIQUE
  subject          text NOT NULL
  body_markdown    text NOT NULL
  status           smallint NOT NULL DEFAULT 0   -- 0=Draft, 1=Sent, 2=Discarded
  edited_by_user_id text NULL FK → AspNetUsers
  sent_utc         timestamptz NULL
  created_utc      timestamptz NOT NULL DEFAULT now()

follow_up_email_recipients
─────────────────────────────────────────────
  id                  uuid PK
  follow_up_email_id  uuid FK → follow_up_emails (CASCADE DELETE)
  recipient_email     text NOT NULL
  recipient_user_id   text NULL FK → AspNetUsers
  sent_at_utc         timestamptz NOT NULL DEFAULT now()

participant_audio_activity             -- for speaker resolution (Phase 6)
─────────────────────────────────────────────
  id                   uuid PK
  meeting_id           uuid FK → meetings (CASCADE DELETE)
  user_id              text FK → AspNetUsers (CASCADE DELETE)
  started_speaking_ms  integer NOT NULL
  stopped_speaking_ms  integer NOT NULL
  INDEX (meeting_id, user_id)

meeting_analytics
─────────────────────────────────────────────
  meeting_id                  uuid PK FK → meetings (CASCADE DELETE)
  total_duration_seconds      integer NOT NULL
  participant_count           integer NOT NULL
  speaking_distribution       jsonb NOT NULL DEFAULT '[]'   -- [{userId, displayName, seconds, pct}]
  word_count                  integer NOT NULL
  average_words_per_minute    double precision NOT NULL
  computed_utc                timestamptz NOT NULL DEFAULT now()
```

---

## 7. System Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                        User's Browser                               │
│  Angular 21 (served by nginx container on the Oracle VM)            │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────────────┐  │
│  │ Dashboard /  │  │ Meeting Room │  │ Meeting Detail           │  │
│  │ History / AI │  │ (LiveKit     │  │ Transcript / Summary /   │  │
│  │ outputs      │  │ video call)  │  │ Action Items / Email     │  │
│  └──────┬───────┘  └──────┬───────┘  └──────────────────────────┘  │
│         │ HTTPS            │ WSS (LiveKit)  │ HTTPS                 │
└─────────┼──────────────────┼──────────────┼────────────────────────┘
          │                  │              │
          ▼                  ▼              ▼
   ┌──────────────────────────────────────────────────────────┐
   │        Oracle Cloud VM  (Ampere A1, Always-Free)         │
   │        smartmeetup.is-a.dev  → Caddy (TLS + routing)     │
   │                                                          │
   │   ┌────────────┐  ┌────────────┐  ┌───────────────────┐  │
   │   │  Caddy     │→ │  nginx     │  │  api (.NET)       │  │
   │   │  :80 :443  │  │  Angular   │  │  AuthController   │  │
   │   │  (auto TLS)│  │  SPA       │  │  MeetingsCtrl     │  │
   │   └─────┬──────┘  └────────────┘  │  SearchCtrl       │  │
   │         │                         │  UsersCtrl        │  │
   │         │ /api/*  /callHub        │  AiProvidersCtrl  │  │
   │         │ /meetingHub /health     │  UserPrefsCtrl    │  │
   │         │ /hangfire /livekit      │  LiveKitWebhook   │  │
   │         │ /logs /storage          │                   │  │
   │         └────────────────────────►│  Hangfire jobs:   │  │
   │                                   │   Transcription   │  │
   │                                   │   AiAnalysis      │  │
   │                                   │   SpeakerMapping  │  │
   │                                   │   Embedding       │  │
   │                                   └───┬─────┬────┬────┘  │
   │                                       │     │    │       │
   │   ┌────────────┐  ┌────────────┐  ┌───▼──┐  │    │       │
   │   │ postgres   │  │ redis      │  │livek. │ │    │       │
   │   │ pgvector 16│◄─┤ (backplane)│  │ SFU  │ │    │       │
   │   │ meetupdb   │  │            │  │+egress│◄┘    │       │
   │   └────────────┘  └────────────┘  └───┬──┘      │       │
   │                                       │         │       │
   │   ┌────────────┐  ┌────────────┐      │         │       │
   │   │ minio      │◄─┤ recordings │◄─────┘         │       │
   │   │ S3-compat  │  │  .ogg      │                │       │
   │   └────────────┘  └────────────┘                │       │
   │                                                 │       │
   │   ┌────────────┐                                │       │
   │   │ seq (logs) │                                │       │
   │   └────────────┘                                │       │
   └─────────────────────────────────────────────────┼───────┘
                                                     │
                                        ┌────────────▼────────────────┐
                                        │  External SaaS (free tiers) │
                                        │                             │
                                        │  Pluggable LLM analysis:    │
                                        │   • Gemini 2.5 Flash (def)  │
                                        │   • OpenAI GPT-4o mini      │
                                        │   • Groq Llama 3.3 70B      │
                                        │   • DeepSeek V3 (OpenRouter)│
                                        │                             │
                                        │  Gemini text-embedding-004  │
                                        │  AssemblyAI (transcription) │
                                        │  Resend (email)             │
                                        └─────────────────────────────┘
```

**Key properties of this topology:**
- Everything except external AI/email APIs runs in one place → simple to reason about, cheap to operate.
- Angular SPA served from the same origin as the API → no CORS drama, single TLS certificate.
- LiveKit UDP (7881/7882) is exposed directly on the VM host (bypassing Caddy) — WebRTC cannot traverse an HTTPS reverse proxy.
- MinIO holds recordings on the same disk — signed URLs go through Caddy's `/storage/` route.
- Redis is present for SignalR/LiveKit coordination even in single-instance mode; makes horizontal scaling later trivial.

---

## 8. Key Design Decisions & Rationale

### 8-A · Why LiveKit instead of keeping the mesh?

The existing WebRTC mesh topology (each participant connects to every other participant directly)
works fine for a demo but has fundamental limitations:

1. **Cannot record.** The server never sees the media — it only relays signaling messages.
   To transcribe meetings, we need the audio on the server.
2. **Hard 5-person cap.** In a mesh, each participant uploads N-1 video streams.
   At 5 people that's 4 uploads + 4 downloads per person. Quality degrades rapidly.
3. **No fault tolerance.** If the host's connection drops, the recording is lost.

With LiveKit, all media flows through the server. The server records natively.
The JavaScript client is simpler (one connection, not N-1 peer connections).

### 8-B · Why PostgreSQL instead of keeping SQL Server?

1. **pgvector** — native vector column type and cosine/dot-product indices.
   This lets us store AI embeddings and do semantic search in the same database
   without adding Pinecone, Qdrant, or Weaviate as separate infrastructure.
2. **tsvector / GIN** — PostgreSQL full-text search is significantly more powerful
   than SQL Server's full-text indexing for our use case (incremental indexing, `ts_rank`).
3. **Runs everywhere** — the `pgvector/pgvector:pg16` container fits on the same Oracle VM
   as everything else. SQL Server is not practical on ARM64 free-tier compute.
4. **Testcontainers.PostgreSql** — tests run against the same DB engine as production
   (the SQLite test container was already a divergence risk).

### 8-C · Why AssemblyAI for transcription?

AssemblyAI offers the best speaker diarization quality in our evaluation.
The `speaker_labels=true` option + `auto_chapters` gives us:
- Per-utterance speaker labels (Speaker A, Speaker B...)
- Chapter markers (topics detected automatically)
- Entity detection (names, companies, dates)
- All via a single asynchronous API call
- 100 hours of free credit at signup — enough for hundreds of demo meetings

OpenAI Whisper (even via the API) does not support diarization natively.
Deepgram is registered as a fallback (45 000 min/month free) if the AssemblyAI credit expires.

### 8-D · Why a pluggable AI analysis provider (with Gemini 2.5 Flash as default)?

Rather than hard-coding a single LLM vendor, the AI analysis step (summary, action items,
decisions, follow-up email) goes through an `IAnalysisProvider` interface with several
interchangeable implementations. Each user picks their preferred provider from a dropdown
in Settings.

**Default: Google Gemini 2.5 Flash** — because:
- **Free tier is real**: 1 500 requests / day, no credit card required.
- **1 M-token context** — a full 1-hour transcript fits without chunking.
- **Native structured output** via `responseSchema` + `responseMimeType: "application/json"`.
- **Same-family embedding model** (`text-embedding-004`) means one vendor for LLM + embeddings.
- **Multilingual** — better than GPT-4o for South Asian languages.

**Alternatives available in the dropdown**:
- OpenAI `gpt-4o-mini` (paid; slightly better assignee resolution)
- Groq `llama-3.3-70b-versatile` (free; fastest response time)
- OpenRouter `deepseek/deepseek-chat` (free tier; strong on reasoning)

Groq and OpenRouter expose OpenAI-compatible APIs — the same `OpenAI` NuGet package is reused
for all three, just with a different `BaseUrl`. Only Gemini uses a separate SDK
(`Mscc.GenerativeAI`).

**Auto-fallback**: if the selected provider throws (rate limit, 5xx), the job retries once,
then falls back to the system default provider. `Meeting.AnalysisProviderUsed` records what
actually ran, so the UI can display it accurately.

**Privacy note**: the Gemini AI Studio free tier may use inputs for training. This is acceptable
for a demo. For handling real customer data, either move to a paid Gemini plan or Vertex AI
(both opt out of training) — no code change beyond swapping the API key.

### 8-E · Why OpenAI structured-output-style schema (for every provider)?

Every provider is asked for JSON matching an explicit schema:
- Gemini uses `generationConfig.responseSchema` — validated by the platform.
- OpenAI/Groq/OpenRouter use `response_format: { type: "json_schema", strict: true }`.
- For providers that only support `type: "json_object"` (older Llama on Groq),
  the schema is inlined in the system prompt and the response is validated with `System.Text.Json`.

This means:
- No regex parsing, no error-prone `JSON.parse` on free-form text.
- The 4 extraction calls (summary, action items, decisions, email) are separate — easier to
  retry individually, cheaper (only the relevant prompt is sent), and observable per-provider.

### 8-F · Why Hangfire for background jobs?

The post-meeting pipeline is: recording ready → transcribe → analyze → embed.
Each step calls an external API that can fail (rate limits, timeouts, API outages).

Hangfire gives us:
- Built-in retry with exponential backoff
- Visual dashboard to inspect failed jobs and re-run them
- PostgreSQL-backed queue (no extra Redis/RabbitMQ required for jobs)
- Simple `.Enqueue<IJob>(j => j.RunAsync(meetingId))` syntax that integrates with .NET DI

### 8-G · Why MinIO for audio storage (instead of Cloudflare R2)?

The demo runs on a single Oracle VM with 200 GB of disk and 10 TB/month of free egress.
Running MinIO in a container beside the API means:
- **Zero external cost**, zero external dependency for the storage tier.
- **S3-compatible API** — the `AWSSDK.S3` client works unchanged; swapping to Cloudflare R2
  (or AWS S3, or Backblaze B2) later is just an endpoint change in `appsettings`.
- LiveKit Egress writes directly via S3 protocol — no transcoding proxy needed.
- Signed URLs are still short-lived (5 min); MinIO enforces the same signature scheme as S3.

For a production launch handling >100 GB of recordings, R2 would still be preferred
(zero egress fees, geo-distributed) — but the codebase only needs to change one connection string.

### 8-H · Why Hangfire over .NET hosted services for the AI pipeline?

A `BackgroundService` or `IHostedService` in .NET has no visibility, no retries, no dashboard.
If the TranscriptionJob fails at 3am, we'd have no idea without checking logs.
Hangfire gives a web dashboard, automatic retries, and the ability for a future admin
to manually re-trigger a job without deploying code.

### 8-I · Why a single Oracle Cloud VM (instead of Fly.io + Cloudflare split)?

The original plan spread the app across four managed services (Fly API, Fly LiveKit,
Fly Postgres, Cloudflare Pages, Cloudflare R2). For a demo, this adds coordination cost
without benefit. The Oracle Always-Free Ampere A1 shape (4 OCPU / 24 GB RAM / 200 GB disk)
comfortably runs every container in the docker-compose stack, forever, for $0.

Trade-offs that are acceptable for a demo:
- **Single point of failure** — an outage takes everything down. Fine for showcase.
- **Manual scaling** — no auto-scaling. Fine for < 50 concurrent users.
- **Manual deploys** — `git pull && docker compose up -d --build` rather than push-to-deploy.

If the demo becomes a real product, the same containers can be split back across
Fly.io + Cloudflare — the code doesn't change, only the compose file and DNS.

---

## 9. Security Considerations

| Risk | Mitigation |
|------|-----------|
| JWT key exposed in source | Moved to .NET user-secrets (dev) and Fly secrets (prod) in Phase 0 |
| Unauthorized access to meeting data | All endpoints require JWT; meeting data filtered by participant membership |
| Unauthorized transcript search | `WHERE MeetingId IN (SELECT ... WHERE UserId = :me)` on every search query |
| LiveKit webhook spoofing | HMAC signature validation on every webhook request |
| AssemblyAI webhook spoofing | Shared secret in request header, validated before processing |
| Signed R2 URLs leaking | Short expiry (5 min); generated per-request, not stored |
| XSS via AI-generated content | All AI text output HTML-escaped before rendering; markdown rendered with a safe renderer (no raw HTML) |
| SQL injection | EF Core parameterized queries throughout; raw SQL only in search with parameterized inputs |
| CSRF | No cookie-based auth; JWT in Authorization header is immune to CSRF |
| Rate abuse (auth endpoints) | Rate limiting per IP on `/api/auth/*` |
| Rate abuse (AI-trigger endpoints) | Rate limiting per user on retry + send-email endpoints |
| Secrets in logs | Serilog destructuring policy excludes password, token, key fields |

---

## 10. API Reference (all endpoints)

### Authentication (`/api/auth`)
| Method | Path | Auth | Description |
|--------|------|------|-------------|
| POST | `/signup` | No | Create account |
| POST | `/login` | No | Login, returns JWT + refresh token |
| POST | `/refresh` | No | Exchange refresh token for new JWT |
| POST | `/logout` | Required | Revoke all refresh tokens |

### Users (`/api/users`)
| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/search?query=` | Required | Search users by name/email |
| PATCH | `/me` | Required | Update display name / preferences |

### User preferences (`/api/me`)
| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/preferences` | Required | Returns `{ preferredAnalysisProviderKey, optOutFollowUpEmails }` |
| PATCH | `/preferences` | Required | Update preferences (validates provider key exists and is configured) |
| GET | `/analytics` | Required | Account-level analytics (hours, topics, etc.) |

### AI providers (`/api/ai`)
| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/providers` | Required | List providers whose API key is configured on the server. Response: `[{ key, displayName, isFree, isDefault, contextWindow, estimatedCostPerMeeting }]` |

### Meetings (`/api/meetings`)
| Method | Path | Auth | Description |
|--------|------|------|-------------|
| POST | `/` | Required | Create meeting; returns LiveKit token |
| GET | `/` | Required | List user's meetings (paginated) |
| GET | `/{id}` | Required | Meeting detail |
| PATCH | `/{id}` | Required (host) | Rename / update |
| DELETE | `/{id}` | Required (host) | Soft delete |
| POST | `/{id}/join` | Required | Join meeting; returns LiveKit token |
| POST | `/{id}/end` | Required (host) | End meeting |
| GET | `/{id}/chat` | Required | Chat message history |
| GET | `/{id}/recording-url` | Required | Get signed download URL for audio |
| GET | `/{id}/transcript` | Required | Get transcript with utterances |
| POST | `/{id}/transcript/retry` | Required (host) | Re-trigger failed transcription |
| GET | `/{id}/summary` | Required | AI-generated summary |
| GET | `/{id}/action-items` | Required | List action items |
| GET | `/{id}/decisions` | Required | List decisions |
| GET | `/{id}/follow-up-email` | Required | Get/view email draft |
| PUT | `/{id}/follow-up-email` | Required (host) | Edit email draft |
| POST | `/{id}/follow-up-email/send` | Required (host) | Send email to participants |
| GET | `/{id}/analytics` | Required | Speaking analytics |

### Action Items (`/api/action-items`)
| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/` | Required | All user's action items (cross-meeting) |
| PATCH | `/{id}` | Required | Update status / assignee / due date |
| DELETE | `/{id}` | Required (host) | Delete action item |

### Search (`/api/search`)
| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/?q=&mode=hybrid` | Required | Hybrid semantic + keyword search |

### User Analytics (`/api/me/analytics`)
_See “User preferences” section above — same base route._

### Webhooks
| Method | Path | Auth | Description |
|--------|------|------|-------------|
| POST | `/api/webhooks/livekit` | HMAC | LiveKit room/participant/egress events |
| POST | `/api/webhooks/assemblyai` | Secret header | AssemblyAI transcript completion event |

### Health
| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/health/live` | No | Liveness check |
| GET | `/health/ready` | No | Readiness check (DB ping) |

### Admin
| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/hangfire` | Admin role | Hangfire job dashboard |
| GET | `/swagger` | Dev only | OpenAPI docs |

---

## 11. Frontend Page Map

```
/                     — Landing page (existing: hero, CTA, sign up / log in links)
/login                — Login page (existing)
/signup               — Sign up page (existing)
/dashboard            — Main dashboard (redesigned):
                          recent meetings, open action items widget, quick-start meeting
/meet/:meetingId      — Live meeting room (video call, chat, recording indicator)
/meeting/:meetingId   — Meeting detail (post-call):
                          Overview | Transcript | Action Items | Decisions | Email | Analytics
/action-items         — Global action items list (cross-meeting, filterable)
/analytics            — Account analytics (hours, trends, topic cloud)
/search               — Search results page
/settings             — User settings (display name, password, email preferences)
```

---

## 12. Post-Meeting Data Flow

```
Meeting ends
     │
     ▼
LiveKit room_finished webhook → Meeting.Status = Processing
     │
     ▼
LiveKit egress_ended webhook → Meeting.RecordingBlobKey set → TranscriptionJob queued
     │
     ▼ (async, ~1-4 min)
TranscriptionJob runs
  → Generates 4h signed MinIO URL for the recording
  → POSTs to AssemblyAI (speaker_labels=true, auto_chapters=true)
  → Waits for completion
  → Saves Transcript + TranscriptUtterance rows
  → Enqueues AiAnalysisJob + SpeakerMappingJob + EmbeddingJob
     │
     ├──▶ AiAnalysisJob (parallel to SpeakerMappingJob + EmbeddingJob)
     │       → Resolves LLM provider (Meeting.AnalysisProviderRequested → Host preference → system default)
     │       → Runs 4 structured-JSON calls in parallel (summary, action items, decisions, email)
     │       → On failure: retries once, then falls back to system default provider
     │       → Saves MeetingSummary, ActionItem[], Decision[], FollowUpEmail
     │       → Sets Meeting.AnalysisProviderUsed to the key that actually ran
     │       → Meeting.Status = Ready
     │
     ├──▶ SpeakerMappingJob
     │       → Correlates ParticipantAudioActivity with TranscriptUtterances
     │       → Resolves SpeakerLabel → UserId
     │       → Saves MeetingAnalytics
     │
     └──▶ EmbeddingJob
             → Chunks transcript into ~300-token segments
             → Batch-embeds via Gemini text-embedding-004 (768 dims)
             → Saves TranscriptChunk rows with Vector columns
```

**Total time from meeting end to results visible:**
- Recording upload: ~10–30s (LiveKit uploads to MinIO as it records; finishes shortly after call ends)
- AssemblyAI transcription: ~1–3 min for a 1h meeting (real-time factor ~0.04x)
- OpenAI analysis: ~15–60s (4 parallel GPT-4o calls)
- **Total: ~2–5 minutes for a typical 30-minute meeting**

---

## 13. Milestones

| Milestone | Phase | Deliverable |
|-----------|-------|------------|
| **M0: Hardened Base** | Phase 0 | Production-shaped codebase; Postgres; secrets; Serilog; tests green |
| **M1: Live on LiveKit** | Phase 1 | Video call works through SFU; mesh code deleted |
| **M2: Persistent Meetings** | Phase 2 | Every call has a DB row; history API; chat persisted |
| **M3: Audio in the Cloud** | Phase 3 | R2 has OGG files; Hangfire running; pipeline triggered |
| **M4: Transcript Ready** | Phase 4 | AI-readable transcript with speaker labels in DB; viewer UI |
| **M5: AI Insights** | Phase 5 | Summary, action items, decisions, email draft generated |
| **M6: Analytics Live** | Phase 6 | Speaking percentages, account analytics, speaker names resolved |
| **M7: Searchable** | Phase 7 | Natural-language search working across meetings |
| **M8: Full UI** | Phase 8 | Dashboard, meeting detail, action items page — all tabs |
| **M9: Email Delivered** | Phase 9 | Host sends follow-up email to all participants |
| **M10: Production** | Phase 10 | Live at smartmeetup.is-a.dev on a single Oracle VM; docker-compose deploy |

---

## 14. Existing Code — What Changes vs. What Stays

### What stays (minimal or no changes)
- `AuthController` — hardened with FluentValidation in Phase 0, otherwise unchanged
- `TokenService` — no changes needed
- `RefreshTokenRepository` — no changes needed
- `MeetingMediaService` (Angular) — used for preview page; hands stream to LiveKit in Phase 1
- `AuthService` (Angular) — no changes
- `authInterceptor` — no changes
- `authGuard` — no changes
- `LoginPage`, `SignupPage`, `HomePage` — no changes

### What is refactored
- `CallHub` → `MeetingHub` — stripped to presence + invites + chat; SDP/ICE removed
- `AppDbContext` — new entities added; provider swapped to Postgres
- `Program.cs` — new services registered each phase
- `UsersController` — injects `IPresenceTracker` instead of calling static `CallHub` method
- `MeetupHome` → split into `DashboardPage` + `MeetingRoomPage`

### What is deleted
- `CallOfferDto` (SDP/ICE DTOs) — replaced by LiveKit SDK
- `SessionDescriptionDto`, `IceCandidateDto` — LiveKit handles these internally
- All raw `RTCPeerConnection` code in `WebRtcPeerService` — deleted after Phase 1

### What is added (all new)
- See the "New Files Summary" section at the bottom of [PHASES.md](./PHASES.md)

---

## 15. Definition of Done (Product Complete)

The product is considered complete when the following end-to-end flow works
on the production deployment (`smartmeetup.is-a.dev`):

1. A new user signs up at `smartmeetup.is-a.dev`
2. They invite a second user to a meeting
3. Both join a 3-person meeting (a third joins via invite)
4. They hold a 5-minute conversation where at least 2 people speak
5. The host ends the meeting
6. Within 5 minutes, the dashboard shows the meeting as **Ready**
7. The meeting detail page shows:
   - A 2–3 paragraph summary of the conversation
   - At least 1 action item with the correct assignee
   - At least 1 decision
   - A draft follow-up email
   - The transcript with speaker names (not "Speaker A/B")
   - A speaking analytics chart
8. The host edits the email draft and clicks Send
9. All participants receive the email in their inbox
10. Searching for a topic discussed in the meeting returns that meeting
11. The dashboard analytics shows updated totals
12. The entire flow works from two different geographic locations
