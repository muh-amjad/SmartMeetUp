# Oracle A1.Flex walkthrough

A copy-paste path from a fresh Ubuntu VM to a working `https://` deployment, using `nip.io` so no
DNS setup is needed. `deploy/README.md` is the reference for what each service does and why; this
file is just the sequence.

Assumes: Oracle Always Free `VM.Standard.A1.Flex`, Ubuntu, code on GitHub, and Gemini + AssemblyAI +
Groq API keys.

---

## 0. Open the ports in the Oracle console — do this first

This is the step that silently breaks everything else. The VCN security list is a **separate
firewall** from the VM's own `ufw`. Traffic never reaches the machine without it, and the symptoms
appear much later ("Caddy can't get a certificate", "the call connects but there's no video").

Navigate to **Networking → Virtual Cloud Networks → your VCN → Security Lists → Default Security
List**, then **Add Ingress Rules**:

| Source CIDR | Protocol | Destination port | Purpose |
|---|---|---|---|
| your own IP `/32` | TCP | 22 | SSH |
| `0.0.0.0/0` | TCP | 80 | certificate issuance and renewal |
| `0.0.0.0/0` | TCP | 443 | HTTPS |
| `0.0.0.0/0` | UDP | 443 | HTTP/3 |
| `0.0.0.0/0` | UDP | 7882 | LiveKit media |
| `0.0.0.0/0` | TCP | 7881 | LiveKit ICE over TCP |
| `0.0.0.0/0` | TCP | 5349 | LiveKit TURN |

Leave *Stateless* unchecked.

> Port 7881 is TCP and 7882 is UDP. They are not interchangeable — 7881 is LiveKit's `rtc.tcp_port`
> and 7882 is `rtc.udp_port`.

---

## 1. Connect and check the machine

```bash
ssh -i <your-key> ubuntu@<vm-public-ip>

uname -m           # aarch64 — expected on A1; every image in the stack has an arm64 build
nproc && free -g   # 4 OCPU / 24 GB on the full free-tier allocation
df -h /            # need ~30 GB free: the egress image alone is 1.4 GB
```

If the boot volume is the 50 GB default and `df` looks tight, expand it in the console
(**Compute → Instance → Boot volume → Edit**) before continuing.

If you were allocated fewer than 4 OCPUs, add swap so the Angular build doesn't get OOM-killed:

```bash
sudo fallocate -l 4G /swapfile && sudo chmod 600 /swapfile
sudo mkswap /swapfile && sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
```

---

## 2. Clone and provision

```bash
sudo apt-get update -y && sudo apt-get install -y git
sudo git clone <your-github-url> /opt/smartmeetup
sudo chown -R ubuntu:ubuntu /opt/smartmeetup
cd /opt/smartmeetup

sudo bash deploy/scripts/provision.sh
```

`provision.sh` installs Docker, clears the pre-seeded iptables REJECT rules that Oracle's Ubuntu
image ships with, configures `ufw`, and installs the nightly backup cron job.

**Log out and back in** so your docker group membership takes effect, then confirm:

```bash
exit
ssh -i <your-key> ubuntu@<vm-public-ip>
docker ps          # should work without sudo
sudo ufw status    # 22, 80, 443, 5349, 7881 tcp; 443, 7882 udp
```

---

## 3. Fill in the environment file

```bash
cd /opt/smartmeetup
cp deploy/.env.example deploy/.env
chmod 600 deploy/.env
```

Generate the secrets in one go and paste the output into `deploy/.env`:

```bash
IP=$(curl -s ifconfig.me)
echo "DOMAIN=$IP.nip.io"
echo "JWT_KEY=$(openssl rand -base64 36)"
echo "POSTGRES_PASSWORD=$(openssl rand -base64 24 | tr -d '/+=')"
echo "LIVEKIT_API_KEY=smartmeetup"
echo "LIVEKIT_API_SECRET=$(openssl rand -base64 36)"
echo "MINIO_ROOT_USER=smartmeetup"
echo "MINIO_ROOT_PASSWORD=$(openssl rand -base64 24 | tr -d '/+=')"
```

Notes on a few of these:

- **`POSTGRES_PASSWORD` and `MINIO_ROOT_PASSWORD` have `/+=` stripped.** They end up inside a
  connection string and an S3 credential, where those characters need escaping. Avoiding them is
  easier than escaping them.
- **`LIVEKIT_API_KEY` is an identifier, not a secret.** The secret is `LIVEKIT_API_SECRET`, which
  must be 32+ characters or `livekit-server` refuses to start.
- **`DOMAIN` uses `nip.io`**, which resolves `<ip>.nip.io` — and `storage.<ip>.nip.io` and
  `logs.<ip>.nip.io` — to that IP. Caddy issues real Let's Encrypt certificates for all three.

Then the rest:

```
ADMIN_EMAIL=amjad.ashfaq999@gmail.com
S3_BUCKET=meetup-recordings
GEMINI_API_KEY=<from aistudio.google.com/apikey>
ASSEMBLYAI_API_KEY=<from assemblyai.com>
GROQ_API_KEY=<from console.groq.com/keys>
EMAIL_FROM=noreply@example.com
RESEND_API_KEY=
```

`ADMIN_EMAIL` is what grants `/hangfire` access — the role is applied at API startup, so sign up
with that address first and then restart the API.

Finally the Seq password (Seq is on a public hostname, so give it a real one):

```bash
docker run --rm datalust/seq config hash 'pick-a-strong-password'
```

Paste the output as `SEQ_ADMIN_PASSWORD_HASH`.

---

## 4. Bring it up

```bash
cd /opt/smartmeetup

# Catches interpolation and syntax errors before anything is built.
docker compose -f deploy/docker-compose.prod.yml config >/dev/null && echo OK

docker compose -f deploy/docker-compose.prod.yml up -d --build
```

First run takes roughly 10–20 minutes on A1: it builds the .NET API and the Angular bundle from
source and pulls ~3 GB of images. Watch it with:

```bash
docker compose -f deploy/docker-compose.prod.yml logs -f
```

Database migrations apply automatically when the API starts.

---

## 5. Verify

```bash
source deploy/.env

docker compose -f deploy/docker-compose.prod.yml ps    # every service Up
curl https://$DOMAIN/health/ready                      # {"status":"Healthy"}
curl -I https://$DOMAIN/                               # 200, valid certificate
curl -I https://storage.$DOMAIN/                       # 403 is correct — TLS works and
                                                       # unsigned requests are refused
curl -I https://logs.$DOMAIN/                          # 200 or a redirect to Seq's login
```

Then in a browser, end to end:

1. Sign up at `https://$DOMAIN`.
2. Start a meeting; join from a second browser or phone.
3. Confirm audio and video both directions, and that `● REC` appears.
4. End the meeting. Within a few minutes the transcript, summary and action items should appear.

Step 3 exercises the UDP path and step 4 exercises webhooks → egress → MinIO → AssemblyAI → Gemini,
which is the whole chain.

---

## 6. If something is wrong

| Symptom | Look here |
|---|---|
| No certificate | Port 80 blocked. Check the VCN list *and* `sudo ufw status`, then `docker compose logs caddy` |
| Call connects, no media | UDP 7882 missing from the VCN ingress list |
| `● REC` never appears | `docker compose logs api \| grep -i webhook` — no entries means LiveKit webhooks aren't arriving |
| Recording exists but won't play | `docker compose logs api \| grep -i storage`; a `SignatureDoesNotMatch` means `BlobStorage__PublicServiceUrl` doesn't match the host serving it |
| Transcript stuck at `Processing` | AssemblyAI must be able to fetch the presigned URL. Copy one from the logs and `curl -I` it — expect 200 |
| Build killed partway | Out of memory. Add swap (step 1) |

---

## Redeploying

```bash
cd /opt/smartmeetup && git pull
docker compose -f deploy/docker-compose.prod.yml up -d --build
```

Under a minute when only application code changed. Migrations run on startup.
