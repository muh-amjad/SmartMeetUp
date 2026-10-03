/**
 * Production build. The app (Vercel) and the API (Render) are on different domains,
 * so both URLs point at the API explicitly.
 */
export const environment = {
  production: true,
  apiBaseUrl: 'https://smartmeetup-api.onrender.com',
  signalrHubUrl: 'https://smartmeetup-api.onrender.com/meetingHub',
};
