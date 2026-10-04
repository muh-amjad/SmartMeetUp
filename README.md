# SmartMeetUp

**Video meetings that write their own notes.** SmartMeetUp is a browser-based video calling app.
After each call it produces a recording, a transcript that attributes every line to a speaker, an
AI summary, action items with owners and due dates, decisions, speaking-time analytics and a
follow-up email. Every meeting stays searchable afterwards.

![SmartMeetUp](docs/case-study/screenshots/00-cover.png)

**Live demo:** [smartmeetup.vercel.app](https://smartmeetup.vercel.app)
&nbsp;·&nbsp; **Case study:** [docs/case-study](docs/case-study/SmartMeetUp-Case-Study.md)

> The demo runs on free hosting tiers. The first request after a quiet period can take up to a
> minute while the API wakes up.

---

## Features

**Calling**
- One-to-one and group video calls over WebRTC (LiveKit SFU), with camera and mic check before joining.
- Calls ring on any page: an incoming-call popup with Accept and Decline and a ringtone generated in the browser.
- Add people to a call that is already in progress. Starting a separate call requires leaving the current one.
- In-call chat, online presence and live speaker highlighting.
- Missed-call and hang-up handling: ringing stops when the caller leaves or after 45 seconds unanswered.

**After the meeting**
- Automatic recording to S3-compatible storage.
- Transcription with speaker diarisation. Anonymous "Speaker A / B" labels are matched to real participants.
- AI summary, key topics, action items (owner and due date), decisions and a follow-up email draft.
- Speaking-time breakdown per participant.
- Meetings stay locked while processing and unlock automatically when their notes are ready. Failed analysis can be retried.

**Across meetings**
- Hybrid search over every transcript, combining keyword and semantic results, with links to the exact moment.
- An action-item tracker and account analytics: meetings per week, time in meetings, recurring topics.

**Platform**
- Fully responsive, dark-themed UI, designed for phones as well as desktops.
- JWT authentication with refresh tokens, per-IP rate limiting and health endpoints.

## Screenshots

| Live call | Incoming call |
| --- | --- |
| ![Call](docs/case-study/screenshots/13-in-call-chat.png) | ![Incoming call](docs/case-study/screenshots/11-incoming-call.png) |
| **AI summary** | **Transcript attributed to each speaker** |
| ![Summary](docs/case-study/screenshots/04-ai-summary.png) | ![Transcript](docs/case-study/screenshots/05-transcript.png) |
| **Search across meetings** | **Analytics** |
| ![Search](docs/case-study/screenshots/09-transcript-search.png) | ![Analytics](docs/case-study/screenshots/08-analytics.png) |

![Mobile views](docs/case-study/screenshots/16-mobile-showcase.png)

## Architecture

![Architecture](docs/case-study/screenshots/17-architecture.png)

- **Frontend:** an Angular single-page app. It handles media through LiveKit and real-time events (presence, invites, chat) through SignalR.
- **Backend:** an ASP.NET Core API. It serves REST endpoints, hosts the SignalR hub and runs the post-meeting pipeline as Hangfire background jobs.
- **Pipeline:** LiveKit webhooks start it: recording → transcription → speaker attribution → LLM analysis → embeddings.

## Tech stack

| Layer | Technologies |
| --- | --- |
| Frontend | Angular 21 (standalone components, signals), TypeScript, LiveKit client, SignalR client, NgRx, GSAP |
| Backend | ASP.NET Core 8, Entity Framework Core 8, ASP.NET Identity + JWT, SignalR, Hangfire, FluentValidation, Serilog |
| Data | PostgreSQL 16 with pgvector (embeddings) and full-text search |
| Media | LiveKit (WebRTC SFU) with room-composite egress for recording |
| AI | AssemblyAI (transcription, diarisation); Google Gemini and any OpenAI-compatible API (OpenRouter, Groq, OpenAI) for analysis; Gemini embeddings |
| Storage and email | S3-compatible object storage (Cloudflare R2 / MinIO); Resend or SMTP |
| Testing | xUnit with `WebApplicationFactory`, Vitest, Playwright |
| Hosting | Vercel (frontend), Render (API, Docker), Neon (PostgreSQL), LiveKit Cloud, Cloudflare R2 |

## Repository structure

```
MeetUpApi/
  MeetUp.Api/           ASP.NET Core API: controllers, SignalR hub, background jobs, services, EF Core migrations
  MeetUp.Api.Tests/     Integration and unit tests
MeetUpUI/
  src/app/              Angular app: pages, components, services, NgRx store
  e2e/                  Playwright end-to-end tests
necessary-files/        Docker Compose stack and LiveKit config for local development
deploy/                 Self-hosted production stack (Docker Compose + Caddy) and deployment guides
docs/case-study/        Case study and screenshots
```

## Getting started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js](https://nodejs.org/) 20.19+ or 22 LTS
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)

### 1. Start the local services

PostgreSQL (with pgvector), LiveKit, LiveKit Egress, MinIO, Redis, Mailpit, Seq and pgAdmin run in
Docker:

```bash
docker compose -f necessary-files/docker-compose.yml up -d
```

The stack is isolated under the Compose project name `smartmeetup-dev`, and its ports are bound to
`127.0.0.1` only.

### 2. Add API keys (optional)

Calls, chat and recording work without any external keys. Transcription, AI analysis and semantic
search need them. Keep keys out of the repository by storing them as .NET user secrets:

```bash
cd MeetUpApi/MeetUp.Api
dotnet user-secrets set "AssemblyAi:ApiKey" "<assemblyai-key>"
dotnet user-secrets set "AiProviders:Providers:gemini-2.5-flash:ApiKey" "<gemini-key>"
```

The Gemini key covers both analysis and embeddings. Any other provider in `appsettings.json` can be
used instead; select it with `AiProviders:Default`.

### 3. Run the API

```bash
cd MeetUpApi/MeetUp.Api
dotnet run --launch-profile http
```

The API listens on `http://localhost:5131` and applies database migrations automatically on startup.
In development, Swagger is available at `/swagger`.

### 4. Run the frontend

```bash
cd MeetUpUI
npm install
npm start
```

Open `http://localhost:4200`, create two accounts (one per browser or private window) and call
between them.

`run-dev.ps1` in the repository root starts the API and the frontend together in separate PowerShell
windows.

### Local URLs

| Service | URL |
| --- | --- |
| App | http://localhost:4200 |
| API / Swagger | http://localhost:5131/swagger |
| Mailpit (caught emails) | http://localhost:8025 |
| MinIO console (recordings) | http://localhost:9001 |
| Seq (logs) | http://localhost:5340 |
| pgAdmin | http://localhost:5050 |

## Configuration

Settings live in `appsettings.json` and `appsettings.Development.json`. In production, every
setting can be overridden with an environment variable, using `__` as the section separator (for
example `Jwt__Key`).

| Setting | Purpose |
| --- | --- |
| `ConnectionStrings__DefaultConnection` | PostgreSQL connection string (pgvector required) |
| `Jwt__Key`, `Jwt__Issuer`, `Jwt__Audience` | Token signing; use a long random key in production |
| `LiveKit__ApiKey`, `LiveKit__ApiSecret`, `LiveKit__WsUrl`, `LiveKit__HttpUrl` | Media server and webhook verification |
| `BlobStorage__*` | S3-compatible bucket for recordings, with optional retention in days |
| `AssemblyAi__ApiKey` | Transcription |
| `AiProviders__Default`, `AiProviders__Providers__<key>__ApiKey` | LLM provider selection and keys |
| `Email__Resend__ApiKey` or `Email__Smtp__*` | Follow-up email delivery |
| `Security__AllowedOrigins__0` | Frontend origin allowed by CORS |
| `Security__RateLimits__*` | Per-IP limits; development defaults allow 10 new meetings per day |

The frontend's API address is set in `MeetUpUI/src/environments/`.

## Testing

```bash
# Backend: integration and unit tests (an in-memory test host, including the SignalR hub)
cd MeetUpApi
dotnet test

# Frontend unit tests (Vitest)
cd MeetUpUI
npm test

# End-to-end tests (Playwright; needs the local services, API and frontend running)
cd MeetUpUI
npm run e2e
```

The end-to-end suite drives several browsers with fake cameras through complete calls: ringing,
accepting, declining, hanging up and adding a third person.

## Deployment

The live demo runs entirely on free tiers:
- **Frontend:** Vercel
- **API:** Render (Docker image from `MeetUpApi/MeetUp.Api/Dockerfile`)
- **Database:** Neon PostgreSQL
- **Media:** LiveKit Cloud
- **Recordings:** Cloudflare R2
- **Transcription and AI:** AssemblyAI and OpenRouter

For a single-server, self-hosted alternative (Docker Compose behind Caddy with automatic TLS), see
[`deploy/README.md`](deploy/README.md).

## License

Copyright © 2026 Muhammad Amjad. All rights reserved.
The source is published for portfolio and evaluation purposes only. No licence is granted to copy,
modify, distribute or use it. See [LICENSE](LICENSE).

**Author:** Muhammad Amjad · [muhammadamjad.dev](https://muhammadamjad.dev)
