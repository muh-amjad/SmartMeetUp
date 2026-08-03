#!/usr/bin/env bash
# One-time bootstrap for a fresh Ubuntu 22.04 VM. Run as root:
#   sudo bash deploy/scripts/provision.sh
#
# Installs Docker, opens the ports the stack needs, and prepares /opt/smartmeetup.
# Idempotent: safe to re-run.

set -euo pipefail

APP_DIR=/opt/smartmeetup
DEPLOY_USER=${SUDO_USER:-ubuntu}

echo "==> Installing packages"
apt-get update -y
apt-get install -y ca-certificates curl gnupg ufw cron git

echo "==> Installing Docker from Docker's own repository"
# Ubuntu's packaged docker.io lags well behind and lacks the compose plugin.
install -m 0755 -d /etc/apt/keyrings
if [ ! -f /etc/apt/keyrings/docker.asc ]; then
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
fi

echo \
  "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] \
https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
  > /etc/apt/sources.list.d/docker.list

apt-get update -y
apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

systemctl enable --now docker

echo "==> Allowing $DEPLOY_USER to run docker without sudo"
usermod -aG docker "$DEPLOY_USER"

echo "==> Configuring the firewall"

# Oracle's Ubuntu images ship a pre-seeded iptables ruleset whose INPUT chain REJECTs everything
# except SSH. It sits in front of ufw, so ufw rules appear to apply while traffic is still dropped —
# which shows up later as "Caddy cannot get a certificate" and "calls connect but have no media",
# with no clue pointing at the firewall. Clear those blanket REJECTs and let ufw be the only
# gatekeeper. Done before ufw is enabled so the box is never left with nothing allowing SSH.
if iptables -S INPUT 2>/dev/null | grep -qE '^-A INPUT .*(REJECT|DROP)'; then
  echo "    Found pre-seeded REJECT/DROP rules in the INPUT chain (typical on Oracle images)."
  echo "    Removing them so ufw governs access on its own."

  while iptables -S INPUT | grep -qE '^-A INPUT .*(REJECT|DROP)'; do
    rule=$(iptables -S INPUT | grep -E '^-A INPUT .*(REJECT|DROP)' | head -1 | sed 's/^-A //')
    # shellcheck disable=SC2086
    iptables -D INPUT $rule
  done

  # Persist, or a reboot brings the blocking rules straight back.
  if command -v netfilter-persistent >/dev/null 2>&1; then
    netfilter-persistent save
  elif [ -f /etc/iptables/rules.v4 ]; then
    iptables-save > /etc/iptables/rules.v4
  fi
fi

ufw allow 22/tcp        comment 'SSH'
ufw allow 80/tcp        comment 'HTTP - required for certificate renewal'
ufw allow 443/tcp       comment 'HTTPS'
ufw allow 443/udp       comment 'HTTP/3'
# WebRTC media cannot pass through the reverse proxy, so LiveKit needs these directly.
ufw allow 7881/udp      comment 'LiveKit RTC'
ufw allow 7882/udp      comment 'LiveKit RTC'
ufw allow 5349/tcp      comment 'LiveKit TURN over TCP'
ufw --force enable

echo "==> Preparing $APP_DIR"
mkdir -p "$APP_DIR" "$APP_DIR/backups"
chown -R "$DEPLOY_USER":"$DEPLOY_USER" "$APP_DIR"

echo "==> Installing the nightly database backup job"
cat > /etc/cron.d/smartmeetup-backup <<CRON
# Nightly pg_dump at 03:15 UTC, keeping 7 days.
15 3 * * * $DEPLOY_USER cd $APP_DIR && bash deploy/scripts/backup-db.sh >> $APP_DIR/backups/backup.log 2>&1
CRON
chmod 0644 /etc/cron.d/smartmeetup-backup

cat <<DONE

Provisioning complete.

  Oracle Cloud reminder: the VCN security list is a separate firewall from ufw. The same ports
  (22, 80, 443 tcp; 443, 7881, 7882 udp; 5349 tcp) must also be opened as ingress rules in the
  console, or traffic never reaches this machine.

Next:
  1. Log out and back in so docker group membership applies.
  2. git clone <your-repo> $APP_DIR
  3. cp deploy/.env.example deploy/.env && chmod 600 deploy/.env   # then fill it in
  4. cd $APP_DIR && docker compose -f deploy/docker-compose.prod.yml up -d --build
DONE
