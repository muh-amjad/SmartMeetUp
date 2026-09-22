# Phase 10 — Production Deployment

Ye file batati hai ke Phase 10 mein **kya banaya**, **kaunse bugs mile**, aur **kaise fix kiye** —
har bug ke saath asli example, taake baad mein padh ke sab samajh aa jaye.

---

## Phase 10 ka maqsad

Baaki saare phases feature banate thay. Ye phase app ko **internet pe chalne layak** banata hai:

1. **Production hardening** — asli code: CORS, security headers, rate limiting, storage retention
2. **Deployment artifacts** — Docker image, compose stack, reverse proxy, scripts, runbook

Ek ahem baat pehle hi saaf kar deta hoon: **VM provision karna aur domain register karna is machine
pe mumkin nahi** (aapka Oracle account aur GitHub chahiye). Isliye jo code/config verify ho sakta
tha wo sab verify kiya, aur baqi ke liye ek runbook likha (`SmartMeetUp/deploy/README.md`).

---

# Hissa 1 — Jo bugs mile

Is phase mein **4 asli bugs** mile. Teen aisi thin jo **sirf chala kar** milti hain — config padh
kar kabhi nahi.

---

## 🐛 Bug #1 — Config eagerly padhna (sabse ahem)

### Kya hua
Rate limiting add karne ke baad **11 tests toot gaye**. Signup pe `429 Too Many Requests` aane laga.

### Pehla khayal (galat)
"Test ka masla hai — limits test ke liye barha do."

### Asli wajah
Maine `Program.cs` mein config **service registration ke waqt** padha tha:

```csharp
// ❌ GALAT
var securityOptions = builder.Configuration
    .GetSection("Security").Get<SecurityOptions>() ?? new SecurityOptions();

builder.Services.AddRateLimiter(limiter =>
    RateLimitPolicies.AddPolicies(limiter, securityOptions.RateLimits));
```

Masla ye hai: **us waqt configuration abhi mukammal nahi hoti.** Jo sources baad mein layer hote hain
— test host ka config, aur **production mein environment variables** — wo is line ko **dikhte hi
nahi**. App chup-chaap default values pe chala jata hai.

### Kyun ye sirf test ka masla nahi tha
Ye production mein bhi tootta:

```yaml
# docker-compose.prod.yml
Security__RateLimits__AuthPerMinute: "50"    # ← ye SILENTLY IGNORE ho jata
```

Aap value set karte, deploy karte, aur app phir bhi default 10 pe chalta rehta — **koi error nahi,
koi warning nahi**. Sabse khatarnak kism ka bug.

### Fix
Config ko **lazily** padho — har request pe:

```csharp
// ✅ SAHI — har request pe current config padhta hai
private static RateLimitPartition<string> Window(HttpContext context, ...)
{
    var limits = context.RequestServices
        .GetRequiredService<IOptionsMonitor<SecurityOptions>>()
        .CurrentValue
        .RateLimits;
    ...
}
```

CORS mein bhi wahi masla tha, wahi tareeqe se fix kiya (`AddOptions<CorsOptions>().Configure<...>`).

### Sabaq
Ye **Phase 7 ke bug ka exact same pattern** hai. Wahan maine `EF.Functions.WebSearchToTsQuery` ko
ek local variable mein rakh diya tha, aur EF client-evaluation pe gir gaya. Dono baar wajah ek hi:
**jo cheez lazily evaluate honi chahiye usay eagerly read kar dena.**

---

## 🐛 Bug #2 — nginx headers ghayab (sirf container chala kar mila)

### Kya hua
Frontend ka Docker image bana. Maine usay **actually run** kiya aur headers check kiye:

```bash
curl -sI http://localhost:8099/
```

Nateeja:
```
Cache-Control: no-cache, no-store, must-revalidate
```

Bas. `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy` — **teeno ghayab**. Jabke maine
unhe `server` block mein likha tha.

### Asli wajah
nginx ka `add_header` inheritance **all-or-nothing** hai:

> Agar kisi block mein **koi bhi** `add_header` ho, to wo parent ke saare `add_header` **inherit
> karna band** kar deta hai.

Meri config aisi thi:

```nginx
server {
    add_header X-Content-Type-Options "nosniff";   # server level
    add_header X-Frame-Options "DENY";

    location = /index.html {
        add_header Cache-Control "no-cache...";     # ← isne upar wale DONO kill kar diye
    }

    location / {
        try_files $uri $uri/ /index.html;           # ← har SPA route yahan se index.html pe jata hai
    }
}
```

Yaani `/`, `/dashboard`, `/meetings` — **har wo page jo user actually kholta hai** — `try_files` se
`location = /index.html` pe pohnchta tha, aur wahan headers gayab thay.

**Sabse buri baat**: headers *theek un responses pe* missing thay jo maayne rakhte hain. Static `.js`
files pe theek thay (wahan bhi apna `add_header` tha... nahi, wahan bhi kill ho rahe thay).

### Fix
`index.html` block mein headers **dobara likhe** (comment ke saath ke kyun):

```nginx
location = /index.html {
    add_header Cache-Control "no-cache, no-store, must-revalidate" always;
    add_header X-Content-Type-Options "nosniff" always;
    add_header X-Frame-Options "DENY" always;
    add_header Referrer-Policy "strict-origin-when-cross-origin" always;
}
```

Verify (re-build + re-run):
```
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
Referrer-Policy: strict-origin-when-cross-origin
```

### Sabaq
Config file padh kar ye bug **kabhi** nahi milta. Container **chala kar `curl` karna** hi iska
sirf ek raasta tha.

---

## 🐛 Bug #3 — Docker image tag jo exist hi nahi karti

### Kya hua
Deployment guide likhne se pehle maine socha: Oracle ka free tier **ARM** hai, to check karun ke
saari images arm64 support karti hain ya nahi. Warna deploy hote hi fail ho jata.

```bash
docker manifest inspect datalust/seq:2024.4
```

Nateeja:
```
no such manifest: docker.io/datalust/seq:2024.4
```

**Ye tag Docker Hub pe maujood hi nahi hai.**

### Ye kahan se aayi
Ye tag **project ke original `docker-compose.yml`** mein pehle se thi (Phase 0/1 se). Maine usay
`docker-compose.prod.yml` mein copy kar diya tha.

Isi liye `docker ps` mein `meetup-seq` container **kabhi nazar nahi aaya** — wo pull hi nahi ho
saka tha. Dev mein kisi ne notice nahi kiya kyunki Seq optional hai (logs Console pe bhi jate hain).

### Kyun ye ahem tha
Production mein `docker compose up -d` **poori tarah fail** hota:
```
Error response from daemon: manifest for datalust/seq:2024.4 not found
```
Pehli hi deploy attempt, aur wajah samajhna mushkil.

### Fix
Available tags check kiye:
```
datalust/seq:2024.3     amd64 arm64
datalust/seq:2025.1     amd64 arm64   ← ye chuna
datalust/seq:latest     amd64 arm64
```

`2025.1` pin kiya (`:latest` nahi — pinned tag reproducible rehta hai). **Dono** compose files
(dev + prod) mein fix kiya.

---

## 🐛 Bug #4 — YAML colon

### Kya hua
```bash
docker compose -f deploy/docker-compose.prod.yml config
```
```
yaml: line 141, column 39: mapping values are not allowed in this context
```

### Wajah
LiveKit ko keys `"key: secret"` format mein chahiye. Maine likha:

```yaml
LIVEKIT_KEYS: ${LIVEKIT_API_KEY}: ${LIVEKIT_API_SECRET}
```

Value ke andar **colon** hai, to YAML usay nested mapping samajh raha tha.

### Fix
```yaml
LIVEKIT_KEYS: "${LIVEKIT_API_KEY}: ${LIVEKIT_API_SECRET}"
```

Chhota bug, lekin `docker compose config` chalaye bina milta hi nahi.

---

## Ek aur cheez jo maine pakdi (bug nahi, gap tha)

`.gitignore` mein **`.env` ka koi rule nahi tha.**

Main `deploy/.env.example` bana raha tha aur runbook mein likh raha tha "`deploy/.env` banao aur
secrets daalo" — aur us file ko commit hone se rokne wala kuch bhi nahi tha. Yaani JWT key,
DB password, saari API keys ek `git add .` se repo mein chali jatin.

Fix + verify:
```gitignore
.env
.env.*
!.env.example
```
```bash
$ git add -An deploy/
add 'deploy/.env.example'      ← ye track hoga
                               ← .env ghayab hai (sahi)
```

---

# Hissa 2 — Jo banaya

## Production hardening (asli code, 6 naye tests)

| Cheez | Kya karta hai | Kyun |
|---|---|---|
| **CORS from config** | Allowed origins `appsettings`/env se | Pehle `localhost:4200` hardcoded tha — production mein har request block hoti |
| **Forwarded headers** | Caddy ke peeche asli client IP + scheme | Warna saare users **ek hi IP** lagte, aur per-IP rate limit sab pe collectively lagta |
| **Security headers** | nosniff, DENY, Referrer-Policy, Permissions-Policy, HSTS | Middleware mein — proxy pe bharosa nahi, kyunki proxy badalne pe guarantee na toote |
| **Rate limiting** | 4 policies (neeche) | Free-tier quotas bachane ke liye |
| **Storage retention** | 30-din lifecycle rule | Recordings hi bulk personal data hain; disk bhi finite hai |

### Rate limits

| Scope | Limit | Partition |
|---|---|---|
| Auth (login/signup/refresh) | 10/min | **per IP** (sign-in se pehle user nahi hota) |
| General API | 100/min | per user |
| AI / email / re-index triggers | 5/min | per user |
| **Meeting creation** | **10/day** | per user |

**Meeting cap sabse ahem hai.** Ek meeting = recording + transcription + AI analysis + embeddings.
Yaani meeting banana hi poore paid pipeline ka darwaza hai. Baqi limits comfort ke liye hain; **ye
wala asal mein paisa/quota bachata hai.**

### Retention `EnsureBucketExistsAsync` mein — ek detail

```csharp
if (!exists) { /* bucket banao */ }

// Har startup pe, sirf creation pe nahi
await ApplyRetentionPolicyAsync(ct);
```

Deliberately bucket creation ke **bahar** rakha. Warna jo bucket retention feature se **pehle** ban
chuka tha, wo hamesha bina rule ke rehta.

### Naye tests (6)

1. Security headers normal response pe
2. Security headers **error** response pe — 401 bhi browser render karta hai
3. HSTS **absent** jab disabled — dev mein bhejna browser ko `https://localhost` pe pin kar deta
4. Auth throttle + `Retry-After` header
5. Meeting daily cap
6. **Ek user ka cap doosre user ko affect na kare** — partitioning ka proof

Testing ke liye **do alag hosts** banaye (`ThrottledAuthFactory`, `ThrottledMeetingsFactory)`.
Wajah: ek hi host mein auth limit 3 rakhne se test ke **apne signups** throttle ho jate thay.

---

## Deployment artifacts

### `MeetUpUI/Dockerfile` + `nginx.conf` — pehli baar bana

Ye gap maine **sabse pehli repo analysis** mein flag kiya tha: *"frontend has no containerized build
path"*. Ab hai:

- Multi-stage build (node → nginx)
- SPA fallback (`try_files ... /index.html`)
- Hashed bundles `immutable` cached, **`index.html` never cached** (warna redeploy ke baad browser
  purane bundle hashes maangta rehta)

**Verify kiya chala kar:**
```
/            → 200
/dashboard   → 200   (SPA fallback)
/nope.js     → 404
main-*.js    → Cache-Control: public, immutable
index.html   → Cache-Control: no-cache
```

### `environment.ts` same-origin

Pehle:
```typescript
apiBaseUrl: 'https://smartmeetup.is-a.dev'   // ❌ domain bundle mein bake ho gaya
```

Ab:
```typescript
apiBaseUrl: ''            // same-origin — Caddy /api/* ko API pe proxy karta hai
signalrHubUrl: '/meetingHub'
```

Fayda: **domain badalne pe rebuild nahi chahiye.**

### `deploy/` folder

| File | Kaam |
|---|---|
| `docker-compose.prod.yml` | 9 services, sab env-driven |
| `Caddyfile` | Reverse proxy + automatic TLS |
| `livekit.prod.yaml` | SFU config (external IP, TURN) |
| `livekit-egress.prod.yaml` | Recording worker |
| `.env.example` | Saare secrets documented, khaali |
| `scripts/provision.sh` | VM bootstrap (Docker, ufw, cron) |
| `scripts/backup-db.sh` | Nightly `pg_dump`, 7 din retention |
| `README.md` | Poora runbook + troubleshooting |

**Security posture**: Postgres, Redis, MinIO, Seq **host pe publish nahi** — sirf internal `expose`.
Publish sirf:
- 80/443 (Caddy)
- UDP 7881/7882 + TCP 5349 (LiveKit media — **WebRTC media HTTPS proxy se guzar nahi sakti**)

---

## Verification — kya actually test hua

| Check | Nateeja |
|---|---|
| Backend tests | ✅ **53/53** (47 se 53) |
| Frontend build + tests | ✅ clean, 10/10 |
| Frontend Docker image | ✅ **build + run + routes + headers** verified |
| `caddy validate` | ✅ **"Valid configuration"** |
| `docker compose config` (prod + dev) | ✅ valid, substitutions correct |
| Shell scripts | ✅ `bash -n` clean |
| **Saari images arm64?** | ✅ verified (Oracle free tier ARM hai) |

### ⚠️ Jo verify NAHI hua

Saaf saaf: **poora stack ek VM pe chalte hue maine nahi dekha.**

1. Oracle VM provision — aapka account chahiye
2. Domain registration — aapke GitHub se PR
3. Asli Let's Encrypt certificate — public DNS chahiye
4. 9 containers ek saath — har piece alag verify kiya, mila kar nahi

---

## Poore project ka tests ka safar

| Phase | Tests | Naya |
|---|---|---|
| Start (Phase 0-2) | 19 | baseline |
| Phase 6 | 23 | +4 (speaker mapping + jsonb) |
| Phase 7 | 31 | +8 (search, scoping, chunker) |
| Phase 8 | 38 | +7 (action items, account) |
| Phase 9 | 47 | +9 (email, XSS, opt-out) |
| **Phase 10** | **53** | +6 (headers, rate limits) |

---

## Har phase mein mila hua sabse bara bug — ek nazar mein

| Phase | Bug | Kaise mila |
|---|---|---|
| 3 | LiveKit SDK mein `AudioCodec` field exist nahi karta | Build fail |
| 4 | Hangfire `[AutomaticRetry]` class pe kaam nahi karta, interface pe chahiye | Logs mein 10 retries dikhe |
| 5 | LLM markdown fences mein JSON wrap karte hain | Runtime |
| 6 | **jsonb write silently fail** — build + migration dono pass | **Test likhne se** |
| 7 | `EF.Functions` hoist karne se client-evaluation | 500 error, exception padh kar |
| 9 | Markdig default mein raw HTML pass karta hai | Security review |
| **10** | **Config eagerly padhna** = env vars silently ignore | **11 tests toote** |

**Pattern**: sabse khatarnak bugs wo thay jinme **kuch bhi fail nahi hota** — Phase 6 ka jsonb aur
Phase 10 ka config. Dono baar build pass, migration pass, koi error nahi — aur feature chup-chaap
kaam nahi karta. Inhe pakadne ka sirf ek raasta tha: **test likhna ya actually chalana.**
