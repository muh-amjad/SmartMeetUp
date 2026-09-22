# MeetUp → AI Meeting Assistant — Implementation Phases

> This document covers every phase of development in detail.
> Each phase lists: goal, all backend tasks, all frontend tasks, new files/entities, and a verification checklist.
> Work through phases in order — each one is a prerequisite for the next.

## Progress Overview

| Phase | Topic | Status |
|---|---|---|
| 0 | Foundation Hardening | ✅ Complete |
| 1 | LiveKit SFU Migration | ✅ Complete |
| 2 | Meeting Persistence & Chat | ✅ Complete |
| 3 | Recording Pipeline (MinIO) | ⏳ Next |
| 4 | Transcription (AssemblyAI) | ⏸ Pending |
| 5 | AI Analysis (pluggable providers) | ⏸ Pending |
| 6 | Speaking Analytics | ⏸ Pending |
| 7 | Search (lexical + semantic) | ⏸ Pending |
| 8 | Dashboard & Meeting-History UI | ⏸ Pending |
| 9 | Follow-up Email Sending | ⏸ Pending |
| 10 | Production Deployment (Oracle Cloud) | ⏸ Pending |

---

## Phase 0 — Foundation Hardening ✅ COMPLETE

**Goal:** Get the existing codebase production-shaped before any AI feature is layered on top.
This phase touches no new domain logic — it is purely about quality, security, and developer experience.

---

### 0-A · Database: SQL Server → PostgreSQL 16 + pgvector

**Why:** pgvector lets us store AI embeddings in the same DB (no separate Pinecone/Qdrant).
PostgreSQL also gives better full-text search, JSON operators, and runs cleanly on the target
Oracle Cloud Always-Free VM (arm64 container available).

**Backend tasks:**
- Remove `Microsoft.EntityFrameworkCore.SqlServer` NuGet package
- Add `Npgsql.EntityFrameworkCore.PostgreSQL` (v8.x) and `Pgvector.EntityFrameworkCore` (v0.3.x)
- Update `AppDbContext` base configuration to use Npgsql conventions (e.g. `UseIdentityColumns`)
- Enable pgvector extension in `OnModelCreating`: `modelBuilder.HasPostgresExtension("vector")`
- Update `Program.cs` DI: `options.UseNpgsql(...)` instead of `UseSqlServer`
- Delete all existing migrations and re-generate `InitialCreate` with the Postgres provider
- Update `appsettings.Development.json` connection string format:
  ```
  Host=localhost;Port=5432;Database=meetupdb;Username=postgres;Password=postgres
  ```
- Add `docker-compose.yml` at repo root:
  - `pgvector/pgvector:pg16` service on port 5432
  - `dpage/pgadmin4` service on port 5050 (optional, for DB browsing in dev)

**Test tasks:**
- Remove SQLite packages from `MeetUp.Api.Tests.csproj`
- Add `Testcontainers.PostgreSql` (v3.x) NuGet
- Rewrite `CustomWebApplicationFactory` to spin up a real Postgres container per test run
  (Testcontainers starts a Docker container, runs migrations, tears down after test class)

**Verification:**
- [ ] `docker compose up` brings up Postgres on :5432
- [ ] `dotnet ef database update` applies migrations without error
- [ ] `dotnet test` passes (all existing auth + hub tests green)
- [ ] `SELECT * FROM "AspNetUsers"` in pgAdmin shows the schema

---

### 0-B · Secrets Out of Source Control

**Why:** JWT signing key is currently committed to `appsettings.json` (a security vulnerability).

**Backend tasks:**
- Run `dotnet user-secrets init` in `MeetUp.Api` project
- Move `Jwt:Key` to user-secrets:
  ```
  dotnet user-secrets set "Jwt:Key" "CHANGE_THIS_TO_A_LONG_SECRET_KEY_FOR_MEETUP_API_2026"
  ```
- Replace the committed key in `appsettings.json` with a placeholder comment / remove it
- Add `appsettings.*.json` entries to `.gitignore` except `appsettings.json` (which should have no real secrets)
- Document the `dotnet user-secrets` setup step in `README.md` under "Local Development Setup"
- In `Program.cs`, confirm `builder.Configuration` already picks up user-secrets in Development (it does by default — just verify)

**Verification:**
- [ ] `appsettings.json` no longer contains a real key
- [ ] App starts correctly in Development using user-secrets
- [ ] CI pipeline (when added in Phase 10) passes the key via environment variable

---

### 0-C · Global Exception Handling + ProblemDetails

**Why:** Currently, unhandled exceptions return HTML error pages (yellow screen of death).
All API errors should return RFC 7807 `application/problem+json`.

**Backend tasks:**
- Create `Infrastructure/Exceptions/` folder with exception types:
  - `NotFoundException(string message)`
  - `ForbiddenException(string message)`
  - `ConflictException(string message)`
  - `ValidationException(IDictionary<string, string[]> errors)`
- Create `Infrastructure/Middleware/GlobalExceptionHandler.cs` implementing `IExceptionHandler`:
  ```csharp
  // Maps each exception type to an appropriate HTTP status + ProblemDetails body
  // Unknown exceptions → 500 with a safe generic message (no stack trace leaked)
  ```
- Register in `Program.cs`:
  ```csharp
  builder.Services.AddProblemDetails();
  builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
  app.UseExceptionHandler();
  ```
- Update `AuthController` and `UsersController` to throw typed exceptions instead of returning raw `BadRequest(string)`
- Add `[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]` attributes

**Verification:**
- [ ] `POST /api/auth/login` with wrong password returns `{"status":401,"title":"Unauthorized","detail":"..."}`
- [ ] Unhandled exception returns 500 with `ProblemDetails`, no stack trace

---

### 0-D · Structured Logging with Serilog

**Why:** Default `ILogger<T>` writes plain text. Serilog writes structured JSON — searchable in log aggregators.

**Backend tasks:**
- Add NuGet packages: `Serilog.AspNetCore`, `Serilog.Sinks.Console`, `Serilog.Sinks.Seq`, `Serilog.Enrichers.Environment`, `Serilog.Enrichers.Thread`
- Replace `builder.Logging` with `builder.Host.UseSerilog(...)` in `Program.cs`
- Configure sinks:
  - Dev: console (pretty-print) + Seq (`http://localhost:5341`)
  - Prod: console + the Seq container running on the same VM (a log aggregator on the host reads stdout too)
- Create `Infrastructure/Logging/UserIdEnricher.cs`:
  Adds `UserId` property to every log entry by reading `IHttpContextAccessor.HttpContext.User`
- Add Seq to `docker-compose.yml` (for dev log browsing)

**Verification:**
- [ ] `dotnet run` writes structured JSON to console
- [ ] Log entries include `RequestId`, `UserId` (when authenticated), timestamp, level
- [ ] Seq UI at `:5341` shows all request logs

---

### 0-E · Request Validation with FluentValidation

**Why:** Auth controller currently does ad-hoc null/length checks inline. FluentValidation separates concerns and gives consistent `ValidationProblemDetails` responses.

**Backend tasks:**
- Add `FluentValidation.AspNetCore` NuGet
- Create `Validators/` folder with:
  - `SignupRequestValidator` — username 3–30 chars, valid email, password min 8 chars with digit + upper + lower
  - `LoginRequestValidator` — not empty
  - `RefreshRequestValidator` — not empty
- Register: `builder.Services.AddValidatorsFromAssemblyContaining<SignupRequestValidator>()`
- Add `[FromBody]` + `IValidator<T>` injection pattern in controllers (or use auto-validation filter)
- Remove inline validation from `AuthController`

**Verification:**
- [ ] `POST /api/auth/signup` with empty password returns `422 Unprocessable Entity` with field-level errors
- [ ] `dotnet test` AuthFlowTests still pass

---

### 0-F · Health Checks

**Backend tasks:**
- Add `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` NuGet
- Register:
  ```csharp
  builder.Services.AddHealthChecks()
      .AddDbContextCheck<AppDbContext>("database");
  ```
- Map endpoints:
  ```csharp
  app.MapHealthChecks("/health/live");   // liveness (app is running)
  app.MapHealthChecks("/health/ready");  // readiness (DB connected)
  ```

**Verification:**
- [ ] `GET /health/ready` returns `200 {"status":"Healthy"}` when DB is up
- [ ] `GET /health/ready` returns `503` when DB is down

---

### 0-G · Refactor CallHub — Typing + IPresenceTracker

**Why:**
1. `CallOfferDto.Offer` is typed as `object` — should be `SessionDescriptionDto`.
2. Static `ConcurrentDictionary` fields directly on `CallHub` cannot be shared across multiple API instances (horizontal scaling).

**Backend tasks:**
- Fix `CallOfferDto`: change `public object Offer { get; set; }` → `public SessionDescriptionDto Offer { get; set; }`
- Create `Services/IPresenceTracker.cs` interface:
  ```csharp
  public interface IPresenceTracker
  {
      void AddUser(string connectionId, UserDto user);
      void RemoveUser(string connectionId);
      UserDto? GetUser(string connectionId);
      IReadOnlyList<UserDto> GetAllUsers();
      bool TryGetConnectionByAppUserId(string appUserId, out string? connectionId);
      // Room management
      string CreateRoom();
      bool TryAddToRoom(string roomId, string appUserId, int maxSize);
      void RemoveFromRoom(string roomId, string appUserId);
      IReadOnlyList<string> GetRoomMembers(string roomId);
      void DeleteRoom(string roomId);
      // Invite management
      void AddInvite(string inviteId, CallInvite invite);
      bool TryGetAndRemoveInvite(string inviteId, out CallInvite? invite);
  }
  ```
- Create `Services/InMemoryPresenceTracker.cs` implementing the interface using the existing `ConcurrentDictionary` logic moved out of `CallHub`
- Register as `AddSingleton<IPresenceTracker, InMemoryPresenceTracker>()`
- Rewrite `CallHub` to inject `IPresenceTracker` instead of using static fields
- Extract `MaxUsersPerRoom` to `Options/MeetingOptions.cs` bound from `appsettings.json`
- Update `UsersController` to inject `IPresenceTracker` instead of calling `CallHub.TryGetOnlineConnectionByAppUserId()` static method

**Verification:**
- [ ] All existing `CallHubTests` still pass
- [ ] `callHub` starts with no static state — every test run is isolated

---

### 0-H · Angular — Environment Files + Error Interceptor + Toast

**Frontend tasks:**
- Create `src/environments/environment.ts`, `environment.development.ts`, `environment.production.ts`
  ```typescript
  export const environment = {
    apiBaseUrl: 'https://smartmeetup.is-a.dev',
    signalrHubUrl: 'https://smartmeetup.is-a.dev/meetingHub',
  };
  ```
- Update `angular.json` `fileReplacements` to swap environment files per build config
- Replace all hardcoded `https://localhost:7248` strings in services with `environment.apiBaseUrl`
- Create `services/toast.service.ts` (simple signal-based notification queue, no external dependency)
- Create `components/toast/toast.component.ts` + template — listens to `ToastService`, renders top-right toasts
- Create `interceptors/http-error.interceptor.ts`:
  - Catches HTTP errors from API
  - Reads `ProblemDetails` body (`error.title`, `error.detail`)
  - Calls `ToastService.error(message)` with the parsed message
  - Re-throws so callers can still handle if needed
- Add `withInterceptors([authInterceptor, httpErrorInterceptor])` in `app.config.ts`
- Add `<app-toast>` to `app.html`

**Verification:**
- [ ] `ng build --configuration=production` compiles without errors, uses prod API URL
- [ ] Invalid login shows a toast with the error detail from ProblemDetails
- [ ] No hardcoded localhost URLs remain in service files

---

### 0-I · Extract WebRtcPeerService

**Why:** All peer-connection logic is embedded inside `MeetupHome` component (~600 lines). Phase 1 replaces this with LiveKit, so it must be extracted first to make the swap clean.

**Frontend tasks:**
- Create `services/webrtc-peer.service.ts` — move out of `MeetupHome`:
  - `peerConnections: Map<string, RTCPeerConnection>`
  - `remoteStreams: Map<string, MediaStream>`
  - `pendingIceCandidates: Map<string, RTCIceCandidateInit[]>`
  - Methods: `createPeerConnection()`, `createOfferFor()`, `handleOffer()`, `handleAnswer()`, `handleCandidate()`, `removeRemotePeer()`, `cleanupAll()`
  - Exposes `remoteVideos: Signal<RemoteVideoItem[]>`
- `MeetupHome` becomes a thin orchestrator that:
  - Injects `WebRtcPeerService`, `SignalrService`, `MeetingMediaService`
  - Wires SignalR events to service methods
  - Displays what the service exposes

**Verification:**
- [ ] Existing Playwright `e2e/call-room.spec.ts` still passes (no behaviour change)
- [ ] `MeetupHome` component file is < 200 lines

---

### Phase 0 Completion Checklist

- [ ] `docker compose up` → Postgres + Seq on localhost
- [ ] `dotnet test` → all green (Testcontainers)
- [ ] `npm run test` → all green
- [ ] `npx playwright test` → all green (unchanged call behaviour)
- [ ] No real secrets in any committed file
- [ ] `GET /health/ready` returns healthy
- [ ] API errors return `ProblemDetails` JSON (not HTML)
- [ ] Structured logs visible in Seq at localhost:5341
- [ ] Angular builds in production mode without errors

---
---

## Phase 1 — Media Server Migration (Mesh → LiveKit SFU) ✅ COMPLETE

**Goal:** Replace the current WebRTC peer-to-peer mesh + signaling-only `CallHub`
with [LiveKit](https://livekit.io/) — an open-source SFU (Selective Forwarding Unit).

**Why LiveKit:**
- Server records every participant's audio track natively (required for transcription)
- Scales beyond 5 users trivially
- Official .NET server SDK + JavaScript/TypeScript client SDK
- Docker-native, self-hostable, managed cloud available
- Every production AI meeting tool (Otter, Fireflies, Read.ai) uses an SFU or equivalent

---

### 1-A · Local LiveKit Infrastructure

**Infra tasks:**
- Add to `docker-compose.yml`:
  ```yaml
  livekit:
    image: livekit/livekit-server:latest
    ports: ["7880:7880", "7881:7881/udp", "7882:7882"]
    volumes: ["./livekit.yaml:/livekit.yaml"]
    command: --config /livekit.yaml
  redis:
    image: redis:7-alpine
    ports: ["6379:6379"]
  ```
- Create `livekit.yaml` at repo root:
  ```yaml
  port: 7880
  rtc:
    tcp_port: 7881
    udp_port: 7882
    use_external_ip: false
  redis:
    address: redis:6379
  keys:
    devkey: devsecret          # API key + secret for local dev
  logging:
    level: debug
  ```
- Add `LiveKit:ApiKey`, `LiveKit:ApiSecret`, `LiveKit:WsUrl` to `appsettings.Development.json`

---

### 1-B · Backend — LiveKit Service + Token Issuance

**Backend tasks:**
- Add NuGet: `LiveKit.Server.Sdk` (official .NET SDK)
- Create `Options/LiveKitOptions.cs`:
  ```csharp
  public class LiveKitOptions {
      public const string SectionName = "LiveKit";
      public string ApiKey { get; set; }
      public string ApiSecret { get; set; }
      public string WsUrl { get; set; }
      public string HttpUrl { get; set; }   // admin REST API base
  }
  ```
- Create `Services/ILiveKitService.cs` and `Services/LiveKitService.cs`:
  ```csharp
  public interface ILiveKitService {
      string GenerateAccessToken(string roomName, string participantIdentity, string displayName, bool isHost);
      Task CreateRoomAsync(string roomName, CancellationToken ct);
      Task EndRoomAsync(string roomName, CancellationToken ct);
      Task StartCompositeEgressAsync(string roomName, string outputKey, CancellationToken ct); // Phase 3
      Task StopEgressAsync(string egressId, CancellationToken ct); // Phase 3
  }
  ```
- Register: `AddScoped<ILiveKitService, LiveKitService>()`

---

### 1-C · Backend — MeetingsController (new)

**New file:** `Controllers/MeetingsController.cs`

**Endpoints:**
| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| `POST` | `/api/meetings` | Required | Create a meeting; returns `meetingId` + LiveKit access token for host |
| `POST` | `/api/meetings/{id}/join` | Required | Get LiveKit access token to join an existing meeting |
| `GET` | `/api/meetings/{id}` | Required | Get meeting details (status, participants) |
| `POST` | `/api/meetings/{id}/end` | Required (host) | Host ends the meeting (LiveKit room close) |

**DTOs to create:**
- `CreateMeetingResponseDto` — `{ meetingId, livekitToken, wsUrl }`
- `JoinMeetingResponseDto` — `{ livekitToken, wsUrl, meetingTitle, participants[] }`

---

### 1-D · Backend — MeetingHub (replaces CallHub)

**Changes to `CallHub.cs` → rename to `MeetingHub.cs`:**

**Remove** (LiveKit now handles these):
- `SendCallOffer()`, `SendCallAnswer()`, `SendCandidate()`
- All SDP/ICE relay logic
- `UpdateMediaState()` (LiveKit handles this via data messages)

**Keep / refactor:**
- `JoinUser()` — still registers presence
- `StartCall()` → `InviteToMeeting(targetUserId, meetingId)` — sends invite to user
- `RespondToCall()` → `RespondToInvite(inviteId, accepted)` — accept/decline
- `StartInstantMeeting()` — creates meeting + invites nobody (user shares a link)

**Add:**
- `SendChatMessage(meetingId, text)` — persists + broadcasts to room

**Client events renamed/added:**
- `ReceiveIncomingCall` → `ReceiveInvite`
- `CallAccepted` → `InviteAccepted` (payload: `{ meetingId, livekitToken, wsUrl }`)
- `CallDeclined` → `InviteDeclined`
- `ChatMessageReceived` — new

**Update `Program.cs`:**
```csharp
app.MapHub<MeetingHub>("/meetingHub");   // was /callHub
```

---

### 1-E · Backend — LiveKit Webhook Handler

**New file:** `Controllers/LiveKitWebhookController.cs`

**Endpoint:** `POST /api/webhooks/livekit`
- Validate HMAC signature using `WebhookReceiver` from SDK (reject if invalid)
- Events to handle:

| LiveKit Event | Action |
|---------------|--------|
| `room_started` | Set `Meeting.ActualStartUtc`, `Status = Live` |
| `room_finished` | Set `Meeting.EndedUtc`, `Status = Processing`; enqueue `TranscriptionJob` (Phase 4) |
| `participant_joined` | Upsert `MeetingParticipant.JoinedUtc` |
| `participant_left` | Set `MeetingParticipant.LeftUtc` |
| `egress_started` | Store `EgressId` on meeting (used for stop command) |
| `egress_ended` | Store `RecordingBlobUrl` + `RecordingDurationSeconds`; enqueue `TranscriptionJob` |

---

### 1-F · Frontend — LiveKit Client Integration

**Frontend tasks:**
- `npm install livekit-client`
- Delete `services/webrtc-peer.service.ts` (extracted in Phase 0)
- Create `services/livekit-meeting.service.ts`:
  ```typescript
  // Wraps LiveKit Room object
  // Exposes:
  //   participants: Signal<RemoteParticipant[]>
  //   localParticipant: Signal<LocalParticipant | null>
  //   connectionState: Signal<ConnectionState>
  //   isRecording: Signal<boolean>
  //
  // Methods:
  //   async joinMeeting(meetingId: string): Promise<void>
  //   async leaveMeeting(): Promise<void>
  //   async toggleCamera(): Promise<void>
  //   async toggleMic(): Promise<void>
  ```
- Update `SignalrService`:
  - Remove all SDP/ICE methods
  - Update hub URL to `/meetingHub`
  - Rename event handlers: `onCallAccepted` → `onInviteAccepted`, etc.
  - `onInviteAccepted` payload now contains `{ meetingId, livekitToken, wsUrl }` → call `livekitMeetingService.joinMeeting(meetingId)`
- Update `MeetupHome` (call mode) to render from `LiveKitMeetingService.participants` signal instead of `WebRtcPeerService.remoteVideos`
- Update `MeetingMediaService.ensureLocalStream()` — still used for preview page; `LiveKitMeetingService.toggleCamera/Mic()` delegates to LiveKit API in the meeting
- Update `MeetupHome` media-control buttons to call `LiveKitMeetingService.toggleCamera/toggleMic()`

**Update routes:**
- `/meet` → `/meet/:meetingId` (meeting ID in URL)

---

### 1-G · Update Playwright E2E Tests

- Update `e2e/call-room.spec.ts` to reflect:
  - LiveKit track subscription events instead of `RTCPeerConnection` events
  - New hub event names (`InviteAccepted` instead of `CallAccepted`)
  - New URL pattern `/meet/:id`

---

### Phase 1 Completion Checklist

- [ ] Two browser windows can connect to the same LiveKit room and hear/see each other
- [ ] LiveKit connection in browser Network tab goes to `ws://localhost:7880`, not peer
- [ ] Camera/mic toggle propagates to the other side (LiveKit data messages)
- [ ] `POST /api/meetings` returns a valid LiveKit token
- [ ] Webhook `room_finished` fires when the meeting ends
- [ ] `MeetingHub` has no SDP/ICE methods
- [ ] `npx playwright test` passes

---
---

## Phase 2 — Meeting Persistence & Domain Model ✅ COMPLETE

**Goal:** Every meeting is a database row with full lifecycle tracking.
Without this, the AI pipeline (Phases 3–7) has nowhere to write results.

---

### 2-A · New Database Entities

**Entities to create:**

```csharp
// Entities/Meeting.cs
public class Meeting {
    public Guid Id { get; set; }
    public string HostUserId { get; set; }
    public ApplicationUser Host { get; set; }
    public string Title { get; set; } = "Untitled Meeting";
    public DateTime? ScheduledStartUtc { get; set; }
    public DateTime? ActualStartUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public MeetingStatus Status { get; set; } = MeetingStatus.Scheduled;
    public string LiveKitRoomName { get; set; }
    public string? RecordingBlobKey { get; set; }      // R2 object key
    public string? RecordingBlobUrl { get; set; }       // signed download URL (updated on fetch)
    public int? RecordingDurationSeconds { get; set; }
    public string? AssemblyAiTranscriptId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public ICollection<MeetingParticipant> Participants { get; set; }
    public ICollection<ChatMessage> ChatMessages { get; set; }
}

public enum MeetingStatus { Scheduled, Live, Ended, Processing, Ready, Failed }

// Entities/MeetingParticipant.cs
public class MeetingParticipant {
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public Meeting Meeting { get; set; }
    public string UserId { get; set; }
    public ApplicationUser User { get; set; }
    public ParticipantRole Role { get; set; }
    public DateTime? JoinedUtc { get; set; }
    public DateTime? LeftUtc { get; set; }
    public int SpeakingSeconds { get; set; }   // computed in Phase 6
}

public enum ParticipantRole { Host, Participant }

// Entities/ChatMessage.cs
public class ChatMessage {
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public Meeting Meeting { get; set; }
    public string SenderUserId { get; set; }
    public ApplicationUser Sender { get; set; }
    public string Text { get; set; }           // max 2000 chars
    public DateTime SentUtc { get; set; }
}
```

**AppDbContext additions:**
```csharp
public DbSet<Meeting> Meetings => Set<Meeting>();
public DbSet<MeetingParticipant> MeetingParticipants => Set<MeetingParticipant>();
public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
```

**EF configuration in `OnModelCreating`:**
- `Meeting`: unique index on `LiveKitRoomName`; auto-update `UpdatedUtc` via interceptor
- `MeetingParticipant`: unique constraint on `(MeetingId, UserId)`
- `ChatMessage`: index on `(MeetingId, SentUtc)`

**Generate migration:** `dotnet ef migrations add AddMeetingDomain`

---

### 2-B · Repositories

Following the existing pattern (`IRepository` + `Repository` classes):

- `IMeetingRepository` + `MeetingRepository`
  - `AddAsync(Meeting meeting, CancellationToken ct)`
  - `GetByIdAsync(Guid id, bool includeParticipants, CancellationToken ct)`
  - `GetUserMeetingsAsync(string userId, int page, int pageSize, CancellationToken ct)`
  - `UpdateAsync(Meeting meeting, CancellationToken ct)`
  - `DeleteAsync(Guid id, CancellationToken ct)`
- `IMeetingParticipantRepository` + `MeetingParticipantRepository`
  - `UpsertAsync(MeetingParticipant participant, CancellationToken ct)`
  - `GetByMeetingAsync(Guid meetingId, CancellationToken ct)`
- `IChatMessageRepository` + `ChatMessageRepository`
  - `AddAsync(ChatMessage message, CancellationToken ct)`
  - `GetByMeetingAsync(Guid meetingId, CancellationToken ct)`

---

### 2-C · Extend MeetingsController

Add to `Controllers/MeetingsController.cs`:

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/meetings` | Paginated list of caller's meetings (host or participant); supports `?status=` filter |
| `GET` | `/api/meetings/{id}` | Full meeting detail |
| `PATCH` | `/api/meetings/{id}` | Rename or reschedule (host only) |
| `DELETE` | `/api/meetings/{id}` | Soft delete (host only) |
| `GET` | `/api/meetings/{id}/chat` | Chat history for meeting |

---

### 2-D · Wire Webhooks to Persistence

Update `LiveKitWebhookController` to call repositories:
- `room_started` → find meeting by `roomName`, set `ActualStartUtc` + `Status = Live`
- `room_finished` → set `EndedUtc`, `Status = Processing`
- `participant_joined` → upsert `MeetingParticipant`
- `participant_left` → set `LeftUtc`

---

### 2-E · Frontend — Meeting Store + Route Update

**Store:**
- Add `meetings` NgRx feature slice:
  - Actions: `loadMeetings`, `loadMeetingsSuccess`, `loadMeeting`, `loadMeetingSuccess`, `updateMeeting`
  - Reducer: `MeetingState { meetings: Meeting[]; selectedMeeting: Meeting | null; loading: boolean }`
  - Effect: `loadMeetings$` → `GET /api/meetings`
- `MeetingsFacade` service (same pattern as `UsersFacade`)

**Services:**
- Create `services/meeting-api.service.ts`:
  - `createMeeting()`, `joinMeeting(id)`, `getMeeting(id)`, `getMeetings(page)`, `endMeeting(id)`, `getChatMessages(id)`

**Routes:**
```typescript
{ path: 'meet/:meetingId', component: MeetingRoomPage, canActivate: [authGuard] },
{ path: 'meeting/:meetingId', component: MeetingDetailPage, canActivate: [authGuard] },
```

**Call flow update:**
- Dashboard "Call" button → `POST /api/meetings` → get `meetingId` → `MeetingHub.InviteToMeeting(targetUserId, meetingId)` → both navigate to `/meet/:meetingId`

---

### Phase 2 Completion Checklist

- [ ] Starting a call creates a `meetings` row in the DB
- [ ] Ending a call sets `EndedUtc` and `Status = Processing`
- [ ] `GET /api/meetings` returns the user's meetings
- [ ] Chat messages sent in a meeting are persisted
- [ ] `MeetingParticipant` rows created on join, `LeftUtc` set on leave

---
---

## Phase 3 — Recording Pipeline ⏳ NEXT

**Goal:** Every ended meeting produces an audio file in blob storage.
LiveKit's Egress service handles the recording; we configure it and react to its completion.
Storage runs on **MinIO** (S3-compatible, self-hosted on the same VM). The interface stays
S3, so swapping to Cloudflare R2 / AWS S3 later is only a connection-string change.

---

### 3-A · Blob Storage Service

**Backend tasks:**
- Add NuGet: `AWSSDK.S3` (MinIO exposes the same S3 API; the SDK works unchanged)
- Create `Options/BlobStorageOptions.cs`:
  ```csharp
  public class BlobStorageOptions {
      public string ServiceUrl { get; set; }        // e.g. http://minio:9000 in-cluster, https://smartmeetup.is-a.dev/storage in prod
      public string AccessKey { get; set; }         // MinIO root user or a scoped key
      public string SecretKey { get; set; }
      public string BucketName { get; set; }
      public bool ForcePathStyle { get; set; } = true;   // required for MinIO
  }
  ```
- Create `Services/IBlobStorageService.cs` + `Services/S3BlobStorageService.cs`:
  ```csharp
  public interface IBlobStorageService {
      Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct);
      Task DeleteAsync(string key, CancellationToken ct);
      Task EnsureBucketExistsAsync(CancellationToken ct);  // called at startup — MinIO buckets are not auto-created
  }
  ```
  (Upload is done by LiveKit Egress directly to MinIO — we only need signed-URL generation, delete, and bucket bootstrap.)
- Register as `AddScoped<IBlobStorageService, S3BlobStorageService>()`
- Add `BlobStorage:*` values to `appsettings.json` (placeholders) and the deploy `.env` on the VM

---

### 3-B · Hangfire Setup

**Backend tasks:**
- Add NuGet: `Hangfire.AspNetCore`, `Hangfire.PostgreSql`
- Register in `Program.cs`:
  ```csharp
  builder.Services.AddHangfire(config =>
      config.UsePostgreSqlStorage(connectionString));
  builder.Services.AddHangfireServer();
  ```
- Map dashboard (admin-only):
  ```csharp
  app.MapHangfireDashboard("/hangfire", new DashboardOptions {
      Authorization = [new HangfireAdminAuthFilter()]
  });
  ```
- Create `Infrastructure/HangfireAdminAuthFilter.cs` — allows only authenticated users with `Admin` role
- Add `Hangfire` DB tables to Postgres via `UsePostgreSqlStorage` (auto-creates schema)

---

### 3-C · Trigger Recording on Meeting Start

**Backend tasks:**
- In `MeetingsController.POST /api/meetings`:
  - After creating the meeting and room in LiveKit, call `ILiveKitService.StartCompositeEgressAsync()`:
    ```
    Room: {roomName}
    Output: S3-compatible endpoint (MinIO), key "recordings/{meetingId}/{utcTimestamp}.ogg"
    AudioOnly: true
    AudioCodec: OGG_OPUS
    ```
  - Store returned `EgressId` on `Meeting.EgressId` (new column in migration)
- Recording starts automatically when the first participant joins the LiveKit room
- LiveKit's S3 sink uses the same MinIO endpoint the API uses; credentials come from the same `.env` file

---

### 3-D · React to Egress Completion

In `LiveKitWebhookController`, on `egress_ended`:
- Update `Meeting.RecordingBlobKey`, `RecordingDurationSeconds`
- Set `Status = Processing`
- Enqueue Hangfire job:
  ```csharp
  BackgroundJob.Enqueue<ITranscriptionJob>(j => j.RunAsync(meeting.Id, CancellationToken.None));
  ```

---

### 3-E · Frontend — Recording Indicator

- In `LiveKitMeetingService`, listen for LiveKit `RoomEvent.RecordingStatusChanged`
- Expose `isRecording: Signal<boolean>`
- In `MeetingRoomPage`, show a small red dot + "● REC" label in the toolbar when recording

---

### Phase 3 Completion Checklist

- [ ] End a meeting → MinIO bucket has a `.ogg` file within 30 seconds
- [ ] `Meeting.RecordingBlobKey` is set in DB
- [ ] `Meeting.Status` transitions: `Live → Processing`
- [ ] Hangfire dashboard at `/hangfire` shows queued `TranscriptionJob`
- [ ] "● REC" indicator visible in the meeting UI while recording
- [ ] The MinIO console (or `mc ls`) shows the object with the expected key

---
---

## Phase 4 — Transcription Pipeline (AssemblyAI)

**Goal:** Convert the audio recording into a diarized, timestamped transcript stored in the database.

---

### 4-A · New Entities

```csharp
// Entities/Transcript.cs
public class Transcript {
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public Meeting Meeting { get; set; }
    public string Language { get; set; } = "en";
    public string FullText { get; set; } = "";     // concatenated plain text (for FTS in Phase 7)
    public string AssemblyAiTranscriptId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public ICollection<TranscriptUtterance> Utterances { get; set; }
}

// Entities/TranscriptUtterance.cs
public class TranscriptUtterance {
    public Guid Id { get; set; }
    public Guid TranscriptId { get; set; }
    public Transcript Transcript { get; set; }
    public string SpeakerLabel { get; set; }         // "A", "B", "C" (AssemblyAI labels)
    public string? ParticipantUserId { get; set; }   // resolved in Phase 6
    public ApplicationUser? Participant { get; set; }
    public int StartMs { get; set; }
    public int EndMs { get; set; }
    public string Text { get; set; }
    public float Confidence { get; set; }
}
```

**AppDbContext additions:** `Transcripts`, `TranscriptUtterances`
**EF Config:** Index on `(TranscriptId, StartMs)`; add tsvector generated column on `Transcript.FullText` (Phase 7 GIN index added there)
**Migration:** `dotnet ef migrations add AddTranscript`

---

### 4-B · AssemblyAI Client Service

**Backend tasks:**
- Add NuGet: `AssemblyAI` (official .NET SDK)
- Create `Options/AssemblyAiOptions.cs`: `ApiKey`
- Create `Services/IAssemblyAiClient.cs` + `Services/AssemblyAiClient.cs`:
  ```csharp
  public interface IAssemblyAiClient {
      Task<string> SubmitTranscriptionAsync(string audioUrl, CancellationToken ct);
      // Returns AssemblyAI transcript ID; use polling or webhook to get result
      Task<AssemblyAiTranscriptResult> GetTranscriptAsync(string transcriptId, CancellationToken ct);
  }
  ```
  Options used: `speaker_labels=true`, `auto_chapters=true`, `entity_detection=true`, `language_detection=true`
- Register as `AddScoped<IAssemblyAiClient, AssemblyAiClient>()`

---

### 4-C · TranscriptionJob (Hangfire)

**New file:** `Jobs/TranscriptionJob.cs`

```
1. Load Meeting from DB (verify Status == Processing)
2. Generate 4-hour signed R2 URL for the recording file
3. POST to AssemblyAI with the signed URL + options
4. Store returned AssemblyAI transcript ID on Meeting
5. Wait for completion:
   - Option A (simpler, robust): Poll AssemblyAI every 15s (max 20 attempts = 5 min) using Hangfire scheduled job
   - Option B (event-driven): Register AssemblyAI webhook pointing to /api/webhooks/assemblyai
6. On transcript ready:
   - Persist Transcript entity
   - Persist all TranscriptUtterance rows (batch insert, 500 at a time)
   - Set Meeting.Status = Ready (tentatively) or leave as Processing until Phase 5 analysis completes
7. Enqueue AiAnalysisJob (Phase 5)
8. Enqueue SpeakerMappingJob (Phase 6)
9. On failure (AssemblyAI error): Set Meeting.Status = Failed; log error; allow manual retry via API
```

---

### 4-D · Retry Endpoint

Add to `MeetingsController`:
- `POST /api/meetings/{id}/transcript/retry` — host only; re-enqueues `TranscriptionJob` if status is `Failed`

---

### 4-E · Frontend — Transcript Viewer

**New component:** `components/transcript-viewer/transcript-viewer.component.ts`
- Fetches `GET /api/meetings/{id}/transcript` (returns ordered utterances)
- Renders scrollable list: speaker badge + text + timestamp (HH:MM:SS)
- Clicking a timestamp emits an event to seek the audio player (Phase 8)
- Speaker labels shown as "Speaker A", "Speaker B" (real names mapped in Phase 6)

**New service endpoint:**
- `GET /api/meetings/{id}/transcript` — returns `{ utterances: TranscriptUtteranceDto[] }`

**Add `<audio>` player to meeting detail page:**
- `GET /api/meetings/{id}/recording-url` → generates a signed download URL (5-min expiry) and returns it
- Frontend fetches this URL, sets as `<audio src="...">`

---

### Phase 4 Completion Checklist

- [ ] End a meeting with 2 speakers → after ~2 min, `Transcript` row exists in DB
- [ ] Multiple speakers produce distinct `SpeakerLabel` values ("A", "B")
- [ ] Transcript viewer UI shows the conversation with timestamps
- [ ] Audio player plays back the recording
- [ ] `POST /api/meetings/{id}/transcript/retry` re-enqueues on failed status
- [ ] Hangfire retries `TranscriptionJob` up to 3 times on AssemblyAI error

---
---

## Phase 5 — AI Analysis (Summary, Action Items, Decisions, Follow-up Email)

**Goal:** After each transcript is ready, extract structured insights (summary, action items,
decisions, follow-up email draft) using a **pluggable LLM provider**. Each user picks their
preferred provider from a Settings dropdown; the analysis job resolves it at runtime and
falls back to the system default on failure.

**Default provider:** Google Gemini 2.5 Flash (free, 1 M-token context, native JSON schema).
**Alternatives day-1:** OpenAI `gpt-4o-mini`, Groq `llama-3.3-70b-versatile`, OpenRouter
`deepseek/deepseek-chat`.

---

### 5-A · New Entities

```csharp
// Entities/MeetingSummary.cs
public class MeetingSummary {
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public Meeting Meeting { get; set; }
    public string OverviewText { get; set; }
    public List<string> KeyTopics { get; set; }    // stored as jsonb
    public string ProviderKey { get; set; }        // which analysis provider produced this (e.g. "gemini-2.5-flash")
    public string ModelUsed { get; set; }          // exact model id reported by the provider
    public DateTime GeneratedUtc { get; set; }
}

// Entities/ActionItem.cs
public class ActionItem {
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public Meeting Meeting { get; set; }
    public string Description { get; set; }
    public string? AssigneeUserId { get; set; }    // FK to AspNetUsers (nullable)
    public ApplicationUser? Assignee { get; set; }
    public string? AssigneeNameRaw { get; set; }   // raw name as the LLM extracted it
    public DateTime? DueDateUtc { get; set; }
    public ActionItemStatus Status { get; set; } = ActionItemStatus.Open;
    public DateTime? CompletedUtc { get; set; }
    public Guid? SourceUtteranceId { get; set; }   // FK to TranscriptUtterance (nullable)
    public TranscriptUtterance? SourceUtterance { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public enum ActionItemStatus { Open, Done, Cancelled }

// Entities/Decision.cs
public class Decision {
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public Meeting Meeting { get; set; }
    public string Description { get; set; }
    public Guid? SourceUtteranceId { get; set; }
    public TranscriptUtterance? SourceUtterance { get; set; }
    public DateTime CreatedUtc { get; set; }
}

// Entities/FollowUpEmail.cs
public class FollowUpEmail {
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public Meeting Meeting { get; set; }
    public string Subject { get; set; }
    public string BodyMarkdown { get; set; }
    public FollowUpEmailStatus Status { get; set; } = FollowUpEmailStatus.Draft;
    public string? EditedByUserId { get; set; }
    public ApplicationUser? EditedBy { get; set; }
    public DateTime? SentUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public enum FollowUpEmailStatus { Draft, Sent, Discarded }
```

**Also extend existing entities (via a small migration):**
```csharp
// ApplicationUser.cs
public string? PreferredAnalysisProviderKey { get; set; }   // null → system default

// Meeting.cs
public string? AnalysisProviderRequested { get; set; }  // captured from Host.PreferredAnalysisProviderKey at meeting creation
public string? AnalysisProviderUsed { get; set; }       // key that actually ran (differs on fallback)
```

**Migration:** `dotnet ef migrations add AddAiAnalysisEntities`

---

### 5-B · Pluggable Analysis Provider Architecture

**NuGets to add:**
- `OpenAI` (v2.x) — powers OpenAI + Groq + OpenRouter (all OpenAI-compatible; only `BaseUrl` differs)
- `Mscc.GenerativeAI` — powers Gemini

**Options binding — `Options/AiProvidersOptions.cs`:**
```csharp
public class AiProvidersOptions {
    public const string Section = "AiProviders";
    public string Default { get; set; } = "gemini-2.5-flash";
    public Dictionary<string, ProviderConfig> Providers { get; set; } = new();
}

public class ProviderConfig {
    public string DisplayName { get; set; }
    public string ApiKey { get; set; }         // env-injected; empty → provider disabled
    public string Model { get; set; }
    public string? BaseUrl { get; set; }       // used by OpenAI-compatible providers only
    public bool IsFree { get; set; }
    public int ContextWindow { get; set; }
}
```

Corresponding `appsettings.json` shape (see Phase 10 `.env` for actual values):
```json
"AiProviders": {
  "Default": "gemini-2.5-flash",
  "Providers": {
    "gemini-2.5-flash":        { "DisplayName": "Google Gemini 2.5 Flash",   "Model": "gemini-2.5-flash",              "IsFree": true,  "ContextWindow": 1000000 },
    "openai-gpt-4o-mini":      { "DisplayName": "OpenAI GPT-4o mini",        "Model": "gpt-4o-mini",                   "BaseUrl": "https://api.openai.com/v1",         "IsFree": false, "ContextWindow": 128000 },
    "groq-llama-3.3-70b":      { "DisplayName": "Groq Llama 3.3 70B",        "Model": "llama-3.3-70b-versatile",       "BaseUrl": "https://api.groq.com/openai/v1",    "IsFree": true,  "ContextWindow": 128000 },
    "openrouter-deepseek-v3":  { "DisplayName": "DeepSeek V3 (via OpenRouter)","Model": "deepseek/deepseek-chat",      "BaseUrl": "https://openrouter.ai/api/v1",      "IsFree": true,  "ContextWindow": 64000 }
  }
}
```

**Shared contract — `Services/AI/IAnalysisProvider.cs`:**
```csharp
public interface IAnalysisProvider {
    string Key { get; }                             // e.g. "gemini-2.5-flash"
    string DisplayName { get; }
    string ModelId { get; }
    Task<SummaryResult> GenerateSummaryAsync(string transcript, CancellationToken ct);
    Task<ActionItemResult[]> ExtractActionItemsAsync(string transcript, IEnumerable<ParticipantInfo> participants, CancellationToken ct);
    Task<DecisionResult[]> ExtractDecisionsAsync(string transcript, CancellationToken ct);
    Task<EmailDraftResult> DraftFollowUpEmailAsync(MeetingContext meeting, CancellationToken ct);
}
```

**Concrete providers** (each in `Services/AI/`):
- `GeminiAnalysisProvider` — uses `Mscc.GenerativeAI` with `responseSchema` + `responseMimeType: "application/json"`
- `OpenAiAnalysisProvider` — uses `OpenAI` SDK, `response_format: { type: "json_schema", strict: true }`
- `GroqAnalysisProvider` — uses `OpenAI` SDK with `BaseUrl` override; falls back to `type: "json_object"` for models that don't support strict schema
- `OpenRouterAnalysisProvider` — uses `OpenAI` SDK with `BaseUrl` override

**Factory + registry:**
- `Services/AI/IAnalysisProviderFactory.cs` + `AnalysisProviderFactory.cs`
  - `Get(string key): IAnalysisProvider`
  - `GetDefault(): IAnalysisProvider`
  - `Resolve(string? requested, string? userPreference): IAnalysisProvider` (fallback chain)
- `Services/AI/AnalysisProviderRegistry.cs`
  - Filters providers by whether `ApiKey` is non-empty
  - Exposes `IReadOnlyList<AnalysisProviderInfo> GetAvailable()` for the API endpoint

**DI registration in `Program.cs`:**
```csharp
builder.Services.Configure<AiProvidersOptions>(builder.Configuration.GetSection(AiProvidersOptions.Section));
builder.Services.AddScoped<IAnalysisProvider, GeminiAnalysisProvider>();
builder.Services.AddScoped<IAnalysisProvider, OpenAiAnalysisProvider>();
builder.Services.AddScoped<IAnalysisProvider, GroqAnalysisProvider>();
builder.Services.AddScoped<IAnalysisProvider, OpenRouterAnalysisProvider>();
builder.Services.AddSingleton<AnalysisProviderRegistry>();
builder.Services.AddScoped<IAnalysisProviderFactory, AnalysisProviderFactory>();
```

**Prompt design principles (shared across providers):**
- Pass participant list (name + userId) so the model can resolve assignee names to IDs
- Temperature: 0.2 for extraction, 0.5 for email drafting
- System prompt instructs the model to return only JSON matching the schema
- Gemini's 1 M-token context handles even 10-hour transcripts without chunking; for smaller-context providers (Llama 3.3, DeepSeek V3) chunk-and-merge if transcript > 60 k tokens

---

### 5-C · AiAnalysisJob (Hangfire)

**New file:** `Jobs/AiAnalysisJob.cs`

```
1. Load Meeting + transcript FullText + utterances + participants from DB
2. Resolve provider via factory:
     a. Meeting.AnalysisProviderRequested  (if set at meeting creation)
     b. Meeting.Host.PreferredAnalysisProviderKey  (fallback)
     c. AiProvidersOptions.Default  (system default)
   → returns IAnalysisProvider `provider`
3. Try provider — run 4 analysis calls in parallel (Task.WhenAll):
     - GenerateSummary
     - ExtractActionItems
     - ExtractDecisions
     - DraftFollowUpEmail
4. If any call throws (rate limit / 5xx / timeout):
     a. Retry once on the same provider
     b. If still failing, re-run all 4 on the system default provider
     c. Log the swap (structured log with both provider keys)
5. Persist MeetingSummary (with ProviderKey + ModelUsed)
6. Persist ActionItem entities (resolve AssigneeNameRaw → AssigneeUserId via participant name match)
7. Persist Decision entities
8. Persist FollowUpEmail entity
9. Set Meeting.AnalysisProviderUsed = actualProvider.Key
10. Set Meeting.Status = Ready
11. On total failure (both provider + fallback): set Status = Failed, allow retry via
    POST /api/meetings/{id}/analysis/retry
```

---

### 5-D · Endpoints

Add to or create controllers:
- `GET /api/meetings/{id}/summary`
- `GET /api/meetings/{id}/action-items`
- `PATCH /api/action-items/{id}` — toggle status, edit description, change assignee, set due date
- `GET /api/meetings/{id}/decisions`
- `GET /api/meetings/{id}/follow-up-email`
- `PUT /api/meetings/{id}/follow-up-email` — host edits draft (subject + body)
- `POST /api/meetings/{id}/analysis/retry` — host only; re-runs `AiAnalysisJob` when `Status = Failed`

**New:** `Controllers/AiProvidersController.cs`
- `GET /api/ai/providers` — returns providers with configured keys:
  `[{ key, displayName, isFree, isDefault, contextWindow }]`

**New:** `Controllers/UserPreferencesController.cs`
- `GET /api/me/preferences` → `{ preferredAnalysisProviderKey, optOutFollowUpEmails }`
- `PATCH /api/me/preferences` — validates that the requested provider key is available before saving

**MeetingsController.POST /api/meetings** update:
- Read the caller's `PreferredAnalysisProviderKey` and store it in `Meeting.AnalysisProviderRequested`
- This freezes the provider choice at meeting-creation time (if the user changes their default later,
  in-flight meetings still use the originally-chosen provider)

---

### 5-E · Frontend — Analysis Tabs (+ provider badge)

Add tabs to `MeetingDetailPage`:

**Overview tab:**
- `MeetingSummary.OverviewText` rendered as formatted text
- Chips for `KeyTopics`
- Small badge in the tab header: **"Analyzed with {providerDisplayName}"** (from `Meeting.AnalysisProviderUsed`)
  - Tooltip on hover: model id + generated timestamp

**Action Items tab:**
- List of `ActionItem` cards:
  - Checkbox to toggle `Done` / `Open`
  - Inline edit for description
  - Assignee avatar/name
  - Due date badge (red if overdue)
  - Click "Source" → jump to utterance in transcript viewer + seek audio

**Decisions tab:**
- Simple list of decision cards with source-utterance link

**Follow-up Email tab:**
- `<textarea>` showing AI draft (markdown rendered as preview)
- "Edit" mode to modify subject/body
- "Send" button (disabled until Phase 9)

_Note: the Settings-page dropdown that lets the user pick the provider is defined in Phase 8-F._

---

### Phase 5 Completion Checklist

- [ ] A 10-minute test meeting generates: summary, ≥0 action items, ≥0 decisions, email draft
- [ ] Action item "assigned to Alice" correctly has `AssigneeUserId` set when Alice is a participant
- [ ] Toggling an action item to Done persists across page refresh
- [ ] `Meeting.Status` becomes `Ready` after analysis completes
- [ ] Frontend shows all 4 tabs with populated content
- [ ] `GET /api/ai/providers` returns only providers whose API key is configured
- [ ] Switching preferred provider to `groq-llama-3.3-70b`, ending a meeting → `Meeting.AnalysisProviderUsed = "groq-llama-3.3-70b"`
- [ ] Meeting detail page shows the correct "Analyzed with X" badge
- [ ] Simulated provider failure (bad API key) triggers auto-fallback to default; log records the swap

---
---

## Phase 6 — Speaking Analytics & Speaker-to-User Resolution

**Goal:** Map AssemblyAI's anonymous "Speaker A/B" labels to real user accounts,
then compute per-participant and per-account speaking analytics.

---

### 6-A · New Entities

```csharp
// Entities/ParticipantAudioActivity.cs
public class ParticipantAudioActivity {
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public Meeting Meeting { get; set; }
    public string UserId { get; set; }
    public ApplicationUser User { get; set; }
    public int StartedSpeakingMs { get; set; }
    public int StoppedSpeakingMs { get; set; }
}

// Entities/MeetingAnalytics.cs
public class MeetingAnalytics {
    public Guid MeetingId { get; set; }   // PK + FK
    public Meeting Meeting { get; set; }
    public int TotalDurationSeconds { get; set; }
    public int ParticipantCount { get; set; }
    public string SpeakingDistributionJson { get; set; }  // jsonb: [{userId, seconds, pct}]
    public int WordCount { get; set; }
    public double AverageWordsPerMinute { get; set; }
    public DateTime ComputedUtc { get; set; }
}
```

---

### 6-B · Capture Active Speaker Events from LiveKit

In `LiveKitWebhookController`, handle `active_speaker_changed` events:
- For each speaker in the event: insert `ParticipantAudioActivity` with `StartedSpeakingMs`
- On the next event that doesn't include that speaker: close the interval (`StoppedSpeakingMs`)

---

### 6-C · SpeakerMappingJob (Hangfire)

**New file:** `Jobs/SpeakerMappingJob.cs`

```
1. Load all TranscriptUtterances for the meeting (with SpeakerLabel)
2. Load all ParticipantAudioActivity for the meeting (with UserId)
3. For each unique SpeakerLabel:
   a. Collect all utterance intervals for that label
   b. For each participant (UserId), sum the overlap between their audio activity and the utterance intervals
   c. Assign the label to the participant with the most overlap
4. Update TranscriptUtterance.ParticipantUserId for all matched utterances
5. Compute MeetingParticipant.SpeakingSeconds from matched utterances
6. Build and save MeetingAnalytics:
   - SpeakingDistributionJson: [{userId, displayName, seconds, pct}]
   - WordCount: count words in FullText
   - AverageWordsPerMinute: WordCount / (TotalDuration / 60)
```

---

### 6-D · Account-Level Analytics Endpoint

- `GET /api/me/analytics?from=2026-01-01&to=2026-12-31`
  - Returns:
    ```json
    {
      "totalMeetingHours": 42.5,
      "meetingCount": 38,
      "averageDurationMinutes": 67,
      "totalSpeakingSeconds": 91800,
      "mostDiscussedTopics": ["Q3 roadmap", "Hiring", "Product launch"],
      "weeklyBreakdown": [...]
    }
    ```

---

### 6-E · Frontend — Analytics Tab + Analytics Page

**Analytics tab on MeetingDetailPage:**
- Horizontal bar chart of speaking distribution (vanilla SVG or Chart.js)
- Total words spoken per participant
- Meeting duration vs. speaking time ratio

**New page `/analytics`:**
- Cards: Total meeting hours | Avg duration | Meetings this month
- Bar/line chart: meetings per week over last 3 months
- Topic tag cloud (top 15 `MostDiscussedTopics` from summaries)

---

### Phase 6 Completion Checklist

- [ ] A 3-speaker meeting produces 3 distinct `SpeakingDistributionJson` entries
- [ ] `TranscriptUtterance.ParticipantUserId` correctly resolved for ≥80% of utterances
- [ ] `/api/me/analytics` returns non-zero totals after a meeting
- [ ] Analytics tab shows speaking distribution chart
- [ ] `/analytics` page renders

---
---

## Phase 7 — Search (Lexical + Semantic / Hybrid)

**Goal:** Users can search "show me every meeting where we discussed pricing"
and get results ranked by relevance, scoped to meetings they attended.

---

### 7-A · New Entity + pgvector Column

**Embedding model**: Google Gemini `text-embedding-004` (free, 768 dimensions).
This is the same vendor family as the default LLM analysis provider, so a single API key
covers both. If a different embedding model is ever needed (e.g. OpenAI 1536-dim), a new
`IEmbeddingProvider` implementation plus a `TranscriptChunk` re-embed migration are required —
the current table stores exactly one vector shape.

```csharp
// Entities/TranscriptChunk.cs
public class TranscriptChunk {
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public Meeting Meeting { get; set; }
    public Guid TranscriptId { get; set; }
    public Transcript Transcript { get; set; }
    public string Text { get; set; }           // ~300 tokens
    public int StartMs { get; set; }
    public int EndMs { get; set; }
    public Vector Embedding { get; set; }      // pgvector Vector(768) — Gemini text-embedding-004
}
```

**EF Config:**
```csharp
entity.Property(c => c.Embedding)
    .HasColumnType("vector(768)");
entity.HasIndex(c => c.Embedding)
    .HasMethod("ivfflat")
    .HasOperators("vector_cosine_ops")
    .HasStorageParameter("lists", 100);
```

**Also add to `Transcript`:**
```csharp
// Generated column for PostgreSQL full-text search
entity.Property(t => t.SearchVector)
    .HasColumnType("tsvector")
    .HasComputedColumnSql("to_tsvector('english', \"FullText\")", stored: true);
entity.HasIndex(t => t.SearchVector)
    .HasMethod("GIN");
```

**Migration:** `dotnet ef migrations add AddSearchInfrastructure`

---

### 7-B · Embedding Service + EmbeddingJob (Hangfire)

**Backend tasks:**
- Reuse the `Mscc.GenerativeAI` NuGet already added in Phase 5-B for Gemini analysis
- Create `Services/AI/IEmbeddingService.cs` + `Services/AI/GeminiEmbeddingService.cs`:
  ```csharp
  public interface IEmbeddingService {
      Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct);
      int Dimensions { get; }   // 768 for text-embedding-004
  }
  ```
- Register as `AddScoped<IEmbeddingService, GeminiEmbeddingService>()`
- Reads API key from the same `AiProviders:Providers:gemini-2.5-flash:ApiKey` (single Google key covers both LLM + embeddings)

**New file:** `Jobs/EmbeddingJob.cs`

```
1. Load Transcript FullText
2. Chunk into ~300-token segments with 50-token overlap (sliding window)
3. Batch embed via Gemini text-embedding-004 (max 100 texts per request)
4. Insert TranscriptChunk rows with 768-dim embeddings
5. On failure: log, allow retry (Hangfire exponential backoff)
```

At query time, the same `IEmbeddingService.EmbedBatchAsync([query])` is used by `HybridSearchService`
so the query vector and stored vectors are always produced by the same model.

---

### 7-C · Search Service

**New file:** `Services/ISearchService.cs` + `Services/HybridSearchService.cs`

```csharp
public interface ISearchService {
    Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, string requestingUserId,
        SearchMode mode,      // Keyword | Semantic | Hybrid
        int limit,
        CancellationToken ct);
}

public class SearchResult {
    public Guid MeetingId { get; set; }
    public string MeetingTitle { get; set; }
    public DateTime MeetingDate { get; set; }
    public string Snippet { get; set; }     // highlighted text context
    public int? StartMs { get; set; }       // for jump-to-moment
    public double Score { get; set; }
}
```

**Hybrid search logic (Reciprocal Rank Fusion):**
1. FTS: `WHERE ts @@ to_tsquery('english', :query)` ORDER BY `ts_rank` LIMIT 20
2. Vector: embed query → `ORDER BY embedding <=> :queryVector` LIMIT 20
3. RRF: `score = 1/(rank_fts + 60) + 1/(rank_vec + 60)` per result; merge + sort
4. ALWAYS add `WHERE MeetingId IN (SELECT MeetingId FROM MeetingParticipants WHERE UserId = :userId)` for security

---

### 7-D · Search Controller

**New file:** `Controllers/SearchController.cs`

- `GET /api/search?q=...&mode=hybrid&limit=10`
  - Authorized
  - Calls `ISearchService.SearchAsync()`
  - Returns `SearchResult[]`

---

### 7-E · Frontend — Search UI

**Update app shell (Phase 8 ships the full shell):**
- Add search input in topbar (visible to authenticated users)
- On submit: navigate to `/search?q=...`

**New page:** `pages/search-results/search-results.page.ts`
- Reads `q` from query params
- Calls `GET /api/search?q=...`
- Renders grouped results (group by meeting)
- Each result shows: meeting title, date, snippet with query terms highlighted, "Jump to moment" link
- "Jump to moment" → navigate to `/meeting/:id?seek=12345` (transcript viewer seeks to `StartMs`)

---

### Phase 7 Completion Checklist

- [ ] Searching "pricing" returns meetings where "cost", "price", "budget" were discussed (semantic mode)
- [ ] Searching exact phrase returns it highlighted in snippet (keyword mode)
- [ ] Results are scoped — user cannot find meetings they were not part of
- [ ] Search completes in < 500ms for a library of 20 meetings
- [ ] "Jump to moment" navigates to correct transcript timestamp

---
---

## Phase 8 — Dashboard & Meeting-History UI

**Goal:** Polish the frontend into a cohesive product.
All AI outputs, history, and analytics accessible from a unified interface.

---

### 8-A · App Shell

**Replace minimal `app.html`** with a proper authenticated layout:
```html
<ng-container *ngIf="isAuthenticated()">
  <app-sidebar />        <!-- left nav: Dashboard, Meetings, Action Items, Analytics, Settings -->
  <main>
    <app-topbar />       <!-- user avatar, search bar, notification bell -->
    <router-outlet />
  </main>
</ng-container>
<router-outlet *ngIf="!isAuthenticated()" />
```

**New components:**
- `components/sidebar/sidebar.component.ts`
- `components/topbar/topbar.component.ts`
- `components/avatar/avatar.component.ts`

---

### 8-B · Redesigned Dashboard (`/dashboard`)

**Sections:**
1. **Quick actions** — "Start instant meeting" card, "Schedule meeting" button (future)
2. **Recent meetings** — last 5 meetings with status badges; click → meeting detail
3. **Open action items** — top 5 open items across all meetings; click → action item detail
4. **This week's analytics** — small cards: hours in meetings, meetings held, avg duration

---

### 8-C · Meeting Detail Page (`/meeting/:id`)

**Tabs:** Overview · Transcript · Action Items · Decisions · Follow-up Email · Analytics

**Common header:**
- Meeting title (inline-edit for host)
- Date, duration, participant chips
- Status badge

**Audio player** (pinned to bottom):
- Standard HTML5 `<audio>` controls
- Transcript viewer + action-item "source" clicks seek this player

**Tab: Overview**
- Summary text
- Key topics chips

**Tab: Transcript**
- `TranscriptViewerComponent` from Phase 4
- Speaker names resolved to real display names once Phase 6 mapping is done

**Tab: Action Items**
- Checkbox list with inline edit, assignee, due date
- "Add manual action item" button

**Tab: Decisions**
- Simple list, each linkable to source utterance

**Tab: Follow-up Email**
- `Subject` input + markdown `<textarea>` for body
- Real-time preview pane (Phase 9 adds Send button)

**Tab: Analytics**
- Speaking time bar chart
- Word count per participant

---

### 8-D · Action Items Page (`/action-items`)

- Global list across all meetings
- Filters: All | Open | Done | Overdue | Assigned to me
- Grouping: by meeting
- Each item: checkbox, meeting link, assignee, due date

---

### 8-E · Analytics Page (`/analytics`)

- Summary cards (from `GET /api/me/analytics`)
- Bar chart: meetings per week (last 12 weeks)
- Topic tag cloud
- "Top meetings by duration" list

---

### 8-F · Settings Page (`/settings`)

Sections (all persisted via `PATCH /api/me/preferences` and `PATCH /api/users/me`):

**Account**
- Display name (edit)
- Password change

**AI analysis model** _(new — driven by Phase 5-D endpoints)_
- Dropdown listing providers returned by `GET /api/ai/providers`
- Each option shows: display name + a small `FREE` / `PAID` chip + context-window hint
- Currently-selected value = `preferences.preferredAnalysisProviderKey` (or the system default if null)
- "Save preference" button → `PATCH /api/me/preferences { preferredAnalysisProviderKey }`
- Copy: _"Applies to your next meeting only. Meetings already analyzed keep their original model."_
- Small info line: _"Analyzed with X"_ badge on the meeting-detail page (Phase 5-E) shows what actually ran

Example markup:
```html
<label>AI analysis model</label>
<select [ngModel]="preference()">
  @for (p of providers(); track p.key) {
    <option [value]="p.key">
      {{ p.displayName }} — {{ p.isFree ? 'FREE' : 'PAID' }} · {{ p.contextWindow | number }} tokens
    </option>
  }
</select>
```

**Notifications**
- Email notification preferences (follow-up email opt-out)

**New Angular files** for this section:
- `services/ai-provider.service.ts` — `getProviders()`, `getPreferences()`, `savePreferences()`
- `dtos/ai-provider.dto.ts`
- `dtos/user-preferences.dto.ts`

---

### 8-G · Route Updates

```typescript
{ path: 'dashboard', component: DashboardPage, canActivate: [authGuard] },
{ path: 'meeting/:meetingId', component: MeetingDetailPage, canActivate: [authGuard] },
{ path: 'meet/:meetingId', component: MeetingRoomPage, canActivate: [authGuard] },
{ path: 'action-items', component: ActionItemsPage, canActivate: [authGuard] },
{ path: 'analytics', component: AnalyticsPage, canActivate: [authGuard] },
{ path: 'search', component: SearchResultsPage, canActivate: [authGuard] },
{ path: 'settings', component: SettingsPage, canActivate: [authGuard] },
```

---

### Phase 8 Completion Checklist

- [ ] New user can sign up, hold a meeting, and navigate the full dashboard within 3 clicks
- [ ] Meeting history list shows correct status badges
- [ ] All 6 meeting-detail tabs render with real data
- [ ] Open action items widget on dashboard shows correct count
- [ ] Analytics page renders charts with real data
- [ ] Audio player pinned and clickable from transcript utterances

---
---

## Phase 9 — Follow-up Email Sending

**Goal:** Host can review the AI-drafted follow-up email and send it to all participants with one click.

---

### 9-A · Backend

**Backend tasks:**
- Add NuGet: `Resend` (community .NET SDK) — thin HTTP client for the Resend API
- Create `Options/ResendOptions.cs`: `ApiKey`, `FromEmail`, `FromName`
  - Default `FromEmail` for demo: `noreply@smartmeetup.is-a.dev`
  - Resend requires domain verification; the `is-a.dev` subdomain can be verified via the DNS TXT record
    added to `domains/smartmeetup.json` alongside the A record
- Create `Services/IEmailService.cs` + `Services/ResendEmailService.cs`:
  ```csharp
  public interface IEmailService {
      Task SendAsync(IEnumerable<string> to, string subject, string htmlBody, CancellationToken ct);
  }
  ```
- Add NuGet: `Markdig` (Markdown → HTML renderer)
- `POST /api/meetings/{id}/follow-up-email/send` (host only):
  1. Load `FollowUpEmail` where `Status == Draft` (409 if already `Sent`)
  2. Load all participant emails (respect `EmailPreference.OptOut`)
  3. Render `BodyMarkdown` → HTML using Markdig
  4. Call `IEmailService.SendAsync(...)`
  5. Set `Status = Sent`, `SentUtc = now`
  6. Log each recipient in new `FollowUpEmailRecipient` table (audit trail)

**Alternative:** the `IEmailService` interface is deliberately provider-agnostic. If Resend's
free tier (3 000/month) is exceeded, drop in a `SendGridEmailService` (SendGrid NuGet) — a
one-line DI swap. Both providers accept the same HTML input.

**New entity:**
```csharp
public class FollowUpEmailRecipient {
    public Guid Id { get; set; }
    public Guid FollowUpEmailId { get; set; }
    public FollowUpEmail FollowUpEmail { get; set; }
    public string RecipientEmail { get; set; }
    public string? RecipientUserId { get; set; }
    public DateTime SentAtUtc { get; set; }
}
```

**New user preference entity:**
```csharp
// Add to ApplicationUser:
public bool OptOutFollowUpEmails { get; set; } = false;
```

---

### 9-B · Frontend

- Enable "Send to all participants" button in email tab
- Show confirmation dialog listing recipient emails before sending
- On success: email tab shows "Sent on [date]" badge
- Disable further edits after sending

**Settings page toggle:** "Receive follow-up emails from meetings I attend" (PATCH `/api/me/preferences`)

---

### 9-C · Dev Testing

- Add `mailcatcher` (or `mailhog`) to the local `docker-compose.yml` — catches all outgoing
  SMTP in dev, web UI on `:1080`
- For dev, `IEmailService` can be swapped to a `SmtpEmailService` pointing at mailcatcher; the
  Resend implementation only runs when `Resend:ApiKey` is set in the environment

---

### Phase 9 Completion Checklist

- [ ] Host sends follow-up → all participating emails receive it (visible in mailcatcher in dev)
- [ ] Re-clicking "Send" returns 409 — email not re-sent
- [ ] Opt-out users are excluded from recipient list
- [ ] `FollowUpEmailRecipient` rows created for audit

---
---

## Phase 10 — Production Deployment (single-VM demo on Oracle Cloud)

**Goal:** Ship the full stack to the public internet at `https://smartmeetup.is-a.dev` using
one Oracle Cloud Always-Free VM, one `docker-compose` file, and Caddy for automatic HTTPS.
Everything except the external AI/email SaaS runs in containers on the same VM.

---

### 10-A · Infrastructure — Oracle Cloud Always-Free VM

**One VM does everything.**

1. Create Oracle Cloud account (card verified, no charges).
2. Provision **VM.Standard.A1.Flex** — 4 OCPU, 24 GB RAM, Ubuntu 22.04, block volume 100 GB.
   - The "Out of capacity" error is common on A1 in popular regions. Script the create call
     to retry every 5 min for up to 24 h. Try Frankfurt / Amsterdam / Phoenix first.
   - **Fallback (documented up front)**: if capacity is unobtainable, pivot to Hetzner CX22
     (€4/mo) or Contabo VPS. Nothing in the compose stack is Oracle-specific.
3. Open VCN ingress security list:
   - TCP 22 (SSH) — restricted to your admin IP
   - TCP 80 + 443 (Caddy HTTP / HTTPS)
   - UDP 7881, 7882 (LiveKit RTC — cannot go through Caddy)
   - TCP 5349 (LiveKit built-in TURN over TCP — needed for restrictive NAT viewers)
4. Bootstrap via `deploy/scripts/provision.sh`:
   ```bash
   apt update && apt install -y docker.io docker-compose-plugin git ufw
   ufw allow 22 && ufw allow 80 && ufw allow 443
   ufw allow 7881/udp && ufw allow 7882/udp && ufw allow 5349/tcp
   ufw enable
   usermod -aG docker ubuntu
   ```

---

### 10-B · Domain & DNS (`smartmeetup.is-a.dev`)

1. Fork `github.com/is-a-dev/register`.
2. Add `domains/smartmeetup.json`:
   ```json
   {
     "owner": { "username": "<gh-user>", "email": "<you>@example.com" },
     "record": { "A": ["<vm-public-ip>"] }
   }
   ```
3. Open PR → maintainer merges (usually hours). DNS resolves globally within ~10 min.
4. **Interim URL while PR is pending**: `https://<ip>.nip.io` — Caddy still issues a real cert
   against it, so full end-to-end demo works before the PR merges.

_Cloudflare proxy is optional; skipped in first deploy for simplicity. Adding it later is
one A→CNAME swap in the is-a.dev JSON._

---

### 10-C · The `docker-compose.prod.yml` stack

Eight containers on the VM, all with `restart: unless-stopped` and pinned tags:

| Service | Image | Purpose |
|---|---|---|
| `caddy` | `caddy:2` | Reverse proxy + auto Let's Encrypt TLS |
| `web` | built from `MeetUpUI/Dockerfile.nginx` (new) | nginx serving the Angular prod build |
| `api` | built from `MeetUpApi/MeetUp.Api/Dockerfile` | .NET API + Hangfire worker in-process |
| `postgres` | `pgvector/pgvector:pg16` | DB + pgvector. Named volume `postgres-data` |
| `redis` | `redis:7-alpine` | Available for future SignalR/LiveKit coordination |
| `livekit` | `livekit/livekit-server:latest` | SFU. Volume-mounts `livekit.yaml` |
| `minio` | `minio/minio:latest` | S3-compatible blob storage (replaces Cloudflare R2). Named volume `minio-data` |
| `seq` | `datalust/seq:2024.4` | Serilog log aggregator; exposed at `/logs/` via Caddy behind an admin login |

All images verified arm64-compatible. `caddy` uses HTTP-01 challenge (port 80 must stay open).

---

### 10-D · Caddyfile — one origin for everything

```
smartmeetup.is-a.dev {
  encode gzip

  # API routes
  reverse_proxy /api/*        api:8080
  reverse_proxy /callHub*     api:8080
  reverse_proxy /meetingHub*  api:8080
  reverse_proxy /health/*     api:8080
  reverse_proxy /hangfire*    api:8080

  # LiveKit signalling (WebSocket only — media UDP bypasses Caddy)
  handle_path /livekit/* { reverse_proxy livekit:7880 }

  # Admin surfaces
  handle_path /logs/*    { reverse_proxy seq:80 }
  handle_path /storage/* { reverse_proxy minio:9000 }

  # SPA — must be last
  reverse_proxy /* web:80
}
```

LiveKit UDP ports 7881/7882 are exposed **directly on the host** because WebRTC media cannot
traverse an HTTPS reverse proxy. TURN-over-TCP on 5349 is exposed the same way for viewers
behind symmetric NAT.

---

### 10-E · Backend production hardening

- **CORS**: single origin `https://smartmeetup.is-a.dev` (via env var)
- **Forwarded headers**: `builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = All)`
  so the API sees the real scheme/IP behind Caddy
- **Disable `UseHttpsRedirection`** inside the API container — Caddy already terminates TLS
- **Security headers** (middleware): `X-Content-Type-Options: nosniff`,
  `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`,
  `Strict-Transport-Security: max-age=63072000`
- **Rate limiting** (`Microsoft.AspNetCore.RateLimiting`):
  - Auth endpoints: 10 req/min per IP
  - AI-trigger endpoints (`/analysis/retry`, `/follow-up-email/send`): 5 req/min per user
  - General API: 100 req/min per user
  - Meeting creation: **soft cap of 10 meetings/user/day** to protect free AI quotas from abuse
- **Presence tracker**: `InMemoryPresenceTracker` stays. No Redis backplane needed until we
  scale to multiple API instances (still one instance in this deployment)
- **MinIO bucket bootstrap**: `IBlobStorageService.EnsureBucketExistsAsync()` runs on startup
  and creates `meetup-recordings` + sets a lifecycle rule to delete objects after 30 days

---

### 10-F · Secrets — `.env` file on the VM (never in git)

`.gitignore` blocks `deploy/.env`. A committed `deploy/.env.example` documents required keys
(empty values). The real file lives at `/opt/smartmeetup/deploy/.env` with `chmod 600`:

```bash
DOMAIN=smartmeetup.is-a.dev

# ── App secrets ──
JWT_KEY=<32+ char random>
POSTGRES_PASSWORD=<random>

# ── AI providers (only fill the ones you want available) ──
GEMINI_API_KEY=AIzaSy...              # free — Google AI Studio (default)
GROQ_API_KEY=gsk_...                  # free — console.groq.com
OPENROUTER_API_KEY=sk-or-...          # free tier
OPENAI_API_KEY=                       # empty → dropdown hides this option

# ── Transcription ──
ASSEMBLYAI_API_KEY=...                # 100 h free credit

# ── Email ──
RESEND_API_KEY=re_...                 # 3 000/month free

# ── LiveKit ──
LIVEKIT_API_KEY=devkey
LIVEKIT_API_SECRET=<random>

# ── MinIO ──
MINIO_ROOT_USER=minio
MINIO_ROOT_PASSWORD=<random>
S3_BUCKET=meetup-recordings
```

`docker compose` reads `.env` automatically and substitutes `${VAR}` references in the compose
file. `.NET` reads them via `IConfiguration` (env vars override `appsettings.json`).

**API-key defence-in-depth**:
- Rotate any key immediately in its provider console if leaked (30-second job)
- Set spending caps in OpenAI/Gemini console ($1–5/mo)
- Restrict Gemini key to VM's public IP in Google Cloud Console
- Enable AssemblyAI / provider usage alerts at 50/80/100 %

---

### 10-G · First deploy

On the VM:
```bash
git clone https://github.com/<you>/SmartMeetUp.git /opt/smartmeetup
cd /opt/smartmeetup
cp deploy/.env.example deploy/.env
chmod 600 deploy/.env
# Fill deploy/.env with real values
docker compose -f deploy/docker-compose.prod.yml up -d
# Migrations run automatically on API startup (already wired in Phase 0)
```

Verify:
- `curl https://smartmeetup.is-a.dev/health/ready` → `{"status":"Healthy"}`
- `curl https://smartmeetup.is-a.dev/api/ai/providers` (with a JWT) lists only providers whose
  key is set — confirms secret plumbing works

---

### 10-H · Ongoing operations (minimal)

- **Redeploy**: `cd /opt/smartmeetup && git pull && docker compose -f deploy/docker-compose.prod.yml up -d --build`
  — completes in < 60 s; API auto-migrates on startup
- **DB backups**: `deploy/scripts/backup-db.sh` runs nightly via cron, `pg_dump` into
  `/opt/smartmeetup/backups/` (kept 7 days)
- **Recording retention**: MinIO lifecycle rule — delete recordings after 30 days
- **Log retention**: Seq container 30-day retention
- **Uptime monitoring**: UptimeRobot free plan pings `/health/ready` every 5 min → email alert
- **Error monitoring**: Sentry optional. Seq (Serilog structured logs) covers the demo

---

### 10-I · Deliberately excluded from this deployment

Compared with the previous Fly.io / Cloudflare-Pages / R2 plan, these are dropped for the demo:
- Redis-backed `IPresenceTracker` (single API instance → not needed)
- SignalR Redis backplane (same reason)
- Cloudflare CDN (optional add-on; skipped for simplicity)
- Cloudflare Pages, Cloudflare R2, Fly.io, Fly Postgres, Fly Redis
- GitHub Actions CI/CD (manual `git pull && docker compose up -d --build` is fine for demo;
  can be added later with the same steps as the old 10-E plan)
- SendGrid (replaced by Resend as default; SendGrid available as drop-in via `IEmailService`)
- Better Stack log aggregator (Seq on-box)
- Honeycomb distributed tracing (optional; can be added post-launch)

---

### Phase 10 Completion Checklist

- [ ] Oracle VM provisioned (or Hetzner fallback documented in commit message)
- [ ] `smartmeetup.is-a.dev` (or `<ip>.nip.io` interim) resolves and shows the Angular app
- [ ] Caddy issued a valid Let's Encrypt certificate; browser shows green padlock
- [ ] Sign-up from two different browsers works; JWT issued
- [ ] Two participants hold a LiveKit call, video + audio flow, recording indicator visible
- [ ] End meeting → within ~3 min → summary + action items + email draft visible in dashboard
- [ ] Meeting detail page shows correct "Analyzed with {provider}" badge
- [ ] `GET /api/ai/providers` lists only providers with configured API keys
- [ ] Rate limits reject the 11th meeting-create request from the same user in a day
- [ ] `pg_dump` cron produces a fresh backup file each night
- [ ] UptimeRobot ping shows green

---
---

## Summary — Phase Dependency Map

```
Phase 0 (Hardening)
  └─ Phase 1 (LiveKit SFU)
       └─ Phase 2 (Meeting Persistence)
            └─ Phase 3 (Recording Pipeline)
                 └─ Phase 4 (Transcription)
                      └─ Phase 5 (AI Analysis)       ←─ Phase 6 (Speaker Analytics)
                           └─ Phase 7 (Search)
                                └─ Phase 8 (Dashboard UI)
                                     └─ Phase 9 (Email Sending)
                                          └─ Phase 10 (Deployment)
```

---

## New Files Summary (all phases)

### Backend (new files)
```
Infrastructure/
  Exceptions/NotFoundException.cs
  Exceptions/ForbiddenException.cs
  Exceptions/ConflictException.cs
  Exceptions/ValidationException.cs
  Middleware/GlobalExceptionHandler.cs
  Logging/UserIdEnricher.cs
  HangfireAdminAuthFilter.cs
Entities/
  Meeting.cs
  MeetingParticipant.cs
  ChatMessage.cs
  Transcript.cs
  TranscriptUtterance.cs
  TranscriptChunk.cs
  MeetingSummary.cs
  ActionItem.cs
  Decision.cs
  FollowUpEmail.cs
  FollowUpEmailRecipient.cs
  ParticipantAudioActivity.cs
  MeetingAnalytics.cs
Controllers/
  MeetingsController.cs
  SearchController.cs
  LiveKitWebhookController.cs
  AssemblyAiWebhookController.cs (optional)
  AiProvidersController.cs
  UserPreferencesController.cs
Services/
  IPresenceTracker.cs
  InMemoryPresenceTracker.cs
  ILiveKitService.cs
  LiveKitService.cs
  IBlobStorageService.cs
  S3BlobStorageService.cs                (MinIO/S3-compatible)
  IAssemblyAiClient.cs
  AssemblyAiClient.cs
  ISearchService.cs
  HybridSearchService.cs
  IEmailService.cs
  ResendEmailService.cs                  (default; SendGridEmailService swappable)
  AI/
    IAnalysisProvider.cs
    GeminiAnalysisProvider.cs            (Mscc.GenerativeAI)
    OpenAiAnalysisProvider.cs            (OpenAI SDK)
    GroqAnalysisProvider.cs              (OpenAI SDK, BaseUrl override)
    OpenRouterAnalysisProvider.cs        (OpenAI SDK, BaseUrl override)
    IAnalysisProviderFactory.cs
    AnalysisProviderFactory.cs
    AnalysisProviderRegistry.cs
    AnalysisProviderInfo.cs
    ProviderResults.cs                   (SummaryResult, ActionItemResult, DecisionResult, EmailDraftResult, ParticipantInfo, MeetingContext)
    IEmbeddingService.cs
    GeminiEmbeddingService.cs
Jobs/
  TranscriptionJob.cs
  AiAnalysisJob.cs
  SpeakerMappingJob.cs
  EmbeddingJob.cs
Options/
  LiveKitOptions.cs
  BlobStorageOptions.cs
  AssemblyAiOptions.cs
  AiProvidersOptions.cs                  (multi-provider config; replaces OpenAiOptions)
  ResendOptions.cs
  MeetingOptions.cs
Dtos/
  UserPreferencesDto.cs
  AiProviderDto.cs
Validators/
  SignupRequestValidator.cs
  LoginRequestValidator.cs
  RefreshRequestValidator.cs
Repositories/
  IMeetingRepository.cs + MeetingRepository.cs
  IMeetingParticipantRepository.cs + MeetingParticipantRepository.cs
  IChatMessageRepository.cs + ChatMessageRepository.cs
  ITranscriptRepository.cs + TranscriptRepository.cs
  IActionItemRepository.cs + ActionItemRepository.cs
```

### Frontend (new files)
```
src/environments/
  environment.ts
  environment.development.ts
  environment.production.ts
src/app/services/
  livekit-meeting.service.ts
  meeting-api.service.ts
  toast.service.ts
  search.service.ts
  analytics.service.ts
  ai-provider.service.ts               (list providers, get/save preferences)
src/app/store/
  meetings/ (actions, reducer, effects, facade, selectors)
  action-items/ (actions, reducer, effects, facade)
src/app/pages/
  dashboard/dashboard.page.ts
  meeting-room/meeting-room.page.ts  (was MeetupHome call mode)
  meeting-detail/meeting-detail.page.ts
  action-items/action-items.page.ts
  analytics/analytics.page.ts
  search-results/search-results.page.ts
  settings/settings.page.ts            (includes AI provider dropdown)
src/app/components/
  sidebar/sidebar.component.ts
  topbar/topbar.component.ts
  toast/toast.component.ts
  transcript-viewer/transcript-viewer.component.ts
  action-item-card/action-item-card.component.ts
  speaking-chart/speaking-chart.component.ts
  ai-provider-badge/ai-provider-badge.component.ts   (shows "Analyzed with X")
src/app/interceptors/
  http-error.interceptor.ts
src/app/dtos/
  ai-provider.dto.ts
  user-preferences.dto.ts
```

### Infra (new files)
```
docker-compose.yml                             (local dev — postgres + seq + mailcatcher)
livekit.yaml                                   (LiveKit config; used both locally and in deploy)
MeetUpUI/Dockerfile.nginx                      (Angular prod build → nginx)
MeetUpApi/MeetUp.Api/appsettings.Production.json
deploy/
  docker-compose.prod.yml                      (all 8 containers for the VM)
  Caddyfile
  .env.example                                 (secret names, empty values — safe to commit)
  scripts/provision.sh                         (idempotent VM bootstrap)
  scripts/backup-db.sh                         (nightly pg_dump cron target)
```
