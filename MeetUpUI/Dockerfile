# ── Build ─────────────────────────────────────────────────────────────
FROM node:22-alpine AS build
WORKDIR /src

# Copy manifests first so the dependency layer is cached until they change.
COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build -- --configuration production

# ── Serve ─────────────────────────────────────────────────────────────
FROM nginx:1.27-alpine AS final

COPY nginx.conf /etc/nginx/conf.d/default.conf

# Angular's application builder emits the browser bundle into a browser/ subfolder.
COPY --from=build /src/dist/MeetUpUI/browser /usr/share/nginx/html

EXPOSE 80

# TLS, compression and routing to the API are Caddy's job in front of this container; nginx only
# has to serve static files and hand unknown paths to the SPA.
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s \
    CMD wget -qO- http://localhost/ >/dev/null 2>&1 || exit 1
