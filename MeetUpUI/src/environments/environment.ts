/**
 * Production build. Both values are same-origin on purpose: Caddy serves the app and proxies
 * /api/* and /meetingHub to the API under one hostname, so relative URLs resolve correctly and the
 * deployment domain is never compiled into the bundle. Changing domain then needs no rebuild.
 */
export const environment = {
  production: true,
  apiBaseUrl: '',
  signalrHubUrl: '/meetingHub'
};
