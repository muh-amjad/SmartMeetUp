# SmartMeetUp — Deploy Walkthrough (pehli baar ke liye)

Ye file **step-by-step** hai, Oracle console ke clicks se le kar chalte hue app tak. Har step ke
baad ek **✅ Checkpoint** hai — usay verify karein, phir agla step.

> Reference runbook (ops, architecture, troubleshooting detail): [`README.md`](README.md)

**Total waqt**: ~45 minute (jismein 10 min build ka intezar hai)

---

## Sab se pehle — ek ahem baat samajh lein

Localhost pe aapki AI features **kaam hi nahi kar sakti thin**. Wajah: AssemblyAI ko recording
**khud fetch** karni hoti hai ek public URL se, aur `localhost:9000` internet se reachable nahi.

**Deploy karna hi wo cheez hai jo transcript → summary → action items → search ko pehli baar
chalayega.** Ye sirf "localhost ko online karna" nahi hai.

---

## Prerequisites checklist

| Cheez | Status |
|---|---|
| Code GitHub pe pushed | ✅ commit `80f40d0` |
| Oracle Cloud account | ✅ ban gaya |
| **Gemini API key** | [aistudio.google.com/apikey](https://aistudio.google.com/apikey) — free, **naya project** banayein (billing wale project pe nahi) |
| **AssemblyAI API key** | [assemblyai.com](https://www.assemblyai.com) — 100 ghante free |
| Groq key (optional) | [console.groq.com](https://console.groq.com) — Gemini rate-limit ho to auto fallback |

⚠️ **Keys ke bagair** deployed app sirf video call + chat dikhayega. Poora "AI meeting assistant"
invisible rahega. Ye do keys pehle le lein.

---

# Step 1 — VM banayein

Oracle console → **☰ → Compute → Instances → Create instance**

### Name
`smartmeetup`

### Image and shape → **Edit**

**Image**: `Change image` → **Canonical Ubuntu** → **22.04**

**Shape**: `Change shape` → **Ampere** tab → `VM.Standard.A1.Flex`
- OCPUs: **4**
- Memory: **24 GB**
- ✅ green **"Always Free eligible"** tag dikhna chahiye

> **`Out of capacity`?** A1 shapes pe bohot aam hai. Dialog band karke thodi der baad retry karein.
> 2–3 din na mile to **Hetzner CX22** (~€4/mah, Germany) le lein — stack mein kuch bhi
> Oracle-specific nahi hai, sirf `DOMAIN` badalna hai.

### Networking
- **Assign a public IPv4 address** → ✅ **on hona zaroori hai**

### Add SSH keys
- **Generate a key pair for me** → **private key download karein** (dobara nahi milegi)
- Ya apni key: `ssh-keygen -t ed25519 -C "smartmeetup"` phir `~/.ssh/id_ed25519.pub` paste karein

**Create** dabayein.

### ✅ Checkpoint 1
- Instance state = **RUNNING**
- **Public IP address copy kar lein** → aage har step mein chahiye

```
Mera Public IP: ________________________
```

---

# Step 2 — VCN firewall (ye step log sab se zyada bhoolte hain)

Oracle mein **do** firewall hain. VM ke andar wala Step 4 handle karega. Ye **console** wala hai —
iske bagair traffic VM tak **pohnchega hi nahi**.

Instance page → scroll → **Primary VNIC** → **Subnet** link → **Security Lists** → default list →
**Add Ingress Rules**

6 rules, har ek mein **Source Type: CIDR**:

| Source CIDR | IP Protocol | Destination Port Range | Kyun |
|---|---|---|---|
| `0.0.0.0/0` | TCP | `22` | SSH |
| `0.0.0.0/0` | TCP | `80` | **Certificate ke liye zaroori** |
| `0.0.0.0/0` | TCP | `443` | HTTPS |
| `0.0.0.0/0` | UDP | `443` | HTTP/3 |
| `0.0.0.0/0` | **UDP** | `7881-7882` | **LiveKit media** |
| `0.0.0.0/0` | TCP | `5349` | TURN (UDP-blocked networks) |

Ek hi dialog mein **"+ Another Ingress Rule"** se saare add kar sakte hain.

### ⚠️ UDP 7881-7882 sab se ahem hai
Ye missing ho to call **connect ho jayegi** (signalling Caddy se guzarti hai) lekin **audio/video
kabhi nahi aayega**. App bilkul theek lagti hai, isliye ye bug dhoondna bohot mushkil hota hai.

### ✅ Checkpoint 2
Security list mein **6 ingress rules** nazar aayein (SSH ka pehle se maujood ho to 5 naye).

---

# Step 3 — SSH

```bash
ssh -i <private-key-ka-path> ubuntu@<PUBLIC_IP>
```

`permissions too open` error aaye to:
```bash
chmod 600 <private-key-ka-path>
```

### ✅ Checkpoint 3
Prompt aisa dikhe: `ubuntu@smartmeetup:~$`

---

# Step 4 — Bootstrap (Docker + firewall + cron)

```bash
git clone https://github.com/muh-amjad/SmartMeetUp.git /opt/smartmeetup
cd /opt/smartmeetup/SmartMeetUp
sudo bash deploy/scripts/provision.sh
```

Ye script:
- Docker + compose plugin install karti hai (Docker ke apne repo se, Ubuntu ka purana `docker.io` nahi)
- `ufw` configure karti hai
- **Oracle ki pre-seeded iptables rules hataati hai** — ye images mein pehle se hoti hain aur `ufw`
  ke **aage** baithti hain, matlab ufw rules lagti *dikhti* hain par traffic phir bhi drop hota hai.
  Iske bagair aap Step 2 sahi karke bhi "certificate nahi mila" mein phans jate.
- Nightly DB backup ka cron lagati hai

Phir **logout + wapas login** (docker group apply hone ke liye):
```bash
exit
ssh -i <key> ubuntu@<PUBLIC_IP>
```

### ✅ Checkpoint 4
```bash
docker ps
```
Khaali table aani chahiye — **koi permission error nahi**. Error aaye to logout/login dobara karein.

---

# Step 5 — Secrets

```bash
cd /opt/smartmeetup/SmartMeetUp
cp deploy/.env.example deploy/.env
chmod 600 deploy/.env
nano deploy/.env
```

Random secrets banane ke liye (doosri terminal ya pehle chala kar copy kar lein):
```bash
openssl rand -base64 36
```

### `deploy/.env` — ye bharein

```bash
# ── Kahan chal raha hai ──
DOMAIN=<PUBLIC_IP>.nip.io          # jaise: 140.238.1.2.nip.io
ADMIN_EMAIL=amjad.ashfaq@a-w.com   # /hangfire access ke liye

# ── App secrets (har ek alag random) ──
JWT_KEY=<random>
POSTGRES_PASSWORD=<random>

# ── LiveKit (inke bagair call nahi chalegi) ──
LIVEKIT_API_KEY=<random>
LIVEKIT_API_SECRET=<random>

# ── Storage ──
MINIO_ROOT_USER=<random>
MINIO_ROOT_PASSWORD=<random>
S3_BUCKET=meetup-recordings

# ── AI (asli value dalein) ──
ASSEMBLYAI_API_KEY=<aapki key>
GEMINI_API_KEY=<aapki key>
GROQ_API_KEY=<optional fallback>
OPENROUTER_API_KEY=
OPENAI_API_KEY=

# ── Email (optional) ──
RESEND_API_KEY=
EMAIL_FROM=noreply@<PUBLIC_IP>.nip.io

# ── Logs ──
SEQ_ADMIN_PASSWORD_HASH=
```

`nano` mein save: `Ctrl+O` → Enter → `Ctrl+X`

### 💡 `nip.io` trick — DNS ka intezar na karein
`DOMAIN=140.238.1.2.nip.io` (apna IP daalein). `nip.io` khud usi IP pe resolve karta hai, aur
**Caddy iske liye asli Let's Encrypt certificate de deta hai**. Poora HTTPS demo abhi chalega.

`smartmeetup.is-a.dev` baad mein chahiye to [is-a-dev/register](https://github.com/is-a-dev/register)
pe PR karein, phir `DOMAIN` badal kar restart. **Rebuild nahi chahiye** — frontend same-origin hai.

### ✅ Checkpoint 5
```bash
grep -c "=" deploy/.env      # kuch lines aani chahiye
ls -l deploy/.env            # permissions -rw------- hon
```

---

# Step 6 — Deploy

```bash
docker compose -f deploy/docker-compose.prod.yml up -d --build
```

**Pehli baar 5–10 minute** lagega: dono images build honge (ARM pe Angular build slow hai) aur Caddy
certificate lega. Sabar karein.

Progress dekhein:
```bash
docker compose -f deploy/docker-compose.prod.yml ps
docker compose -f deploy/docker-compose.prod.yml logs -f caddy
```

Caddy logs mein **`certificate obtained successfully`** dhoondein. (`Ctrl+C` se logs se nikal jayein —
containers chalte rahenge.)

### ✅ Checkpoint 6
```bash
docker compose -f deploy/docker-compose.prod.yml ps
```
9 services `Up` hone chahiye: `caddy web api postgres redis livekit livekit-egress minio seq`

```bash
curl https://<PUBLIC_IP>.nip.io/health/ready
```
Expected: `{"status":"Healthy"}`

---

# Step 7 — Demo verify karein

Browser mein: `https://<PUBLIC_IP>.nip.io` → **green padlock** aana chahiye.

### Demo flow (isi tarteeb se)

1. **Signup** karein — wahi email jo `ADMIN_EMAIL` mein daali thi
2. **Doosra browser** (ya incognito) → doosra account banayein
3. Pehle account se: dashboard pe **"Call a teammate"** → doosre ka username search → **Call**
4. Doosre browser mein invite banner → **Accept**
5. **Audio/video check karein** + top pe **`● REC`** indicator dekhein
6. Thodi baat karein (2-3 jumle — transcript ke liye content chahiye)
7. **End Call**
8. **2–3 minute rukein** ← ye zaroori hai, background jobs chal rahe hain
9. **Meetings** → us meeting pe click karein

### Ab ye sab dikhna chahiye

| Tab | Kya |
|---|---|
| **Overview** | AI summary + key topics + "Analyzed with Gemini" badge |
| **Transcript** | Asli naamon ke saath (Speaker A nahi), timestamps clickable |
| **Action Items** | Jo commitments hui, checkbox ke saath |
| **Decisions** | Jo tay hua |
| **Follow-up Email** | Draft — send button (Resend key ho to) |
| **Analytics** | Kis ne kitna bola, bar chart |

Aur topbar se **search** karein — koi lafz jo meeting mein bola tha. Semantic bhi chalegi (Gemini key se).

### 🎉 Yehi pehla moment hai jab poora AI pipeline actually chalta hai
Localhost pe ye mumkin nahi tha.

---

# Troubleshooting

Pehle logs dekhein:
```bash
cd /opt/smartmeetup/SmartMeetUp
docker compose -f deploy/docker-compose.prod.yml logs api | tail -50
docker compose -f deploy/docker-compose.prod.yml logs livekit-egress | tail -30
docker compose -f deploy/docker-compose.prod.yml logs caddy | tail -30
```

| Alamat | Sabab | Fix |
|---|---|---|
| Certificate nahi mila / HTTPS nahi | Port **80** band | VCN rule #2 check karein; `sudo ufw status` |
| Site khulti hai par API fail | `api` container down | `logs api` — aksar `.env` mein koi value missing |
| Call connect hui, **audio/video nahi** | **UDP 7881-7882 band** | VCN rule #5 — sab se aam ghalti |
| `● REC` nahi aata | Egress start nahi hua | `logs livekit-egress` |
| Transcript `Processing` pe atka | AssemblyAI recording fetch nahi kar saka | `curl -I https://<DOMAIN>/storage/` — reachable hona chahiye |
| Transcript aaya, AI kuch nahi | Gemini key missing/invalid | `logs api \| grep -i analysis` |
| `/hangfire` pe 401 | Admin role nahi mila | `ADMIN_EMAIL` wale account se signup karke API restart karein |

### Redeploy (code update ke baad)
```bash
cd /opt/smartmeetup/SmartMeetUp
git pull
docker compose -f deploy/docker-compose.prod.yml up -d --build
```
Migrations khud chalti hain. ~1 minute.

### Sab kuch restart
```bash
docker compose -f deploy/docker-compose.prod.yml restart
```

---

# Demo ke baad — do ahem baatein

### 1. Recordings mein asli meeting audio hoti hai
Ye personal data hai. Bucket private hai (presigned URLs), aur 30 din baad auto-delete ho jati hai.
Jaldi hatana ho to `deploy/.env` mein retention kam kar dein, ya MinIO se manually delete karein.

### 2. Public app hai — koi bhi signup kar sakta hai
Rate limits lagi hui hain (**meeting creation 10/din per user**), jo free AI quotas bachati hain.
Lekin demo khatam hone ke baad app band kar dena behtar hai:

```bash
docker compose -f deploy/docker-compose.prod.yml down
```

Data volumes mein rehta hai — `up -d` se wapas chal jayega.

### Cost
- Oracle Always Free A1 → **$0** (jab tak Always Free limits ke andar hain)
- Gemini / AssemblyAI free tiers → **$0**
- Ek hi cheez ka khayal: agar Gemini key kisi **billing-enabled** project se banayi ho to quota
  khatam hone pe charge lag sakta hai. Naye project (bina billing) se banayein.

---

# Quick reference card

```bash
# Sab kuch kahan hai
cd /opt/smartmeetup/SmartMeetUp
COMPOSE="docker compose -f deploy/docker-compose.prod.yml"

$COMPOSE ps                  # status
$COMPOSE logs -f api         # live logs
$COMPOSE restart api         # ek service restart
$COMPOSE up -d --build       # redeploy
$COMPOSE down                # band karein

# Health
curl https://<DOMAIN>/health/ready

# Backup manually
bash deploy/scripts/backup-db.sh

# Restore
gunzip -c backups/<file>.sql.gz | docker exec -i smartmeetup-postgres psql -U postgres -d smartmeetupdb
```

### URLs

| Kya | Kahan |
|---|---|
| App | `https://<DOMAIN>/` |
| Health | `https://<DOMAIN>/health/ready` |
| Logs (Seq) | `https://<DOMAIN>/logs/` |
| Background jobs | `https://<DOMAIN>/hangfire` (Admin role chahiye) |
