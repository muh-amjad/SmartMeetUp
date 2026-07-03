import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  timeout: 90_000,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  use: {
    baseURL: 'http://localhost:4200',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    ignoreHTTPSErrors: true,
    launchOptions: {
      args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream'],
    },
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
  webServer: [
    {
      command: 'dotnet run --project d:/MeetUpProject/MeetUpApi/MeetUp.Api/MeetUp.Api.csproj --urls https://localhost:7248',
      url: 'https://localhost:7248/swagger/index.html',
      reuseExistingServer: true,
      timeout: 120_000,
      ignoreHTTPSErrors: true,
    },
    {
      command: 'npm run start -- --host localhost --port 4200',
      url: 'http://localhost:4200',
      cwd: 'd:/MeetUpProject/MeetUpUI',
      reuseExistingServer: true,
      timeout: 120_000,
    },
  ],
});
