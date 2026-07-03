import { expect, Page, test } from '@playwright/test';

type TestUser = {
  username: string;
  email: string;
  password: string;
};

function makeUser(prefix: string): TestUser {
  const suffix = `${Date.now()}-${Math.floor(Math.random() * 1_000_000)}`;
  return {
    username: `${prefix}-${suffix}`,
    email: `${prefix}-${suffix}@meetup.test`,
    password: 'Password123!',
  };
}

async function signupAndLogin(page: Page, user: TestUser): Promise<void> {
  await page.goto('/signup');

  await page.getByLabel('Username').fill(user.username);
  await page.getByLabel('Email').fill(user.email);
  await page.getByLabel('Password').fill(user.password);
  await page.getByRole('button', { name: 'Sign up' }).click();

  await expect(page).toHaveURL(/\/login$/);
  await page.getByLabel('Username or email').fill(user.username);
  await page.getByLabel('Password').fill(user.password);
  await page.getByRole('button', { name: 'Log in' }).click();

  await expect(page).toHaveURL(/\/dashboard$/);
}

async function callUserBySearch(callerPage: Page, targetUsername: string): Promise<void> {
  await callerPage.getByPlaceholder('Search by username or email').fill(targetUsername);
  await callerPage.getByRole('button', { name: /^Search$/ }).click();

  const searchItem = callerPage.locator('.search-item', { hasText: targetUsername }).first();
  await expect(searchItem).toBeVisible();
  await searchItem.getByRole('button', { name: /^Call$/ }).click();
}

async function acceptIncomingCall(page: Page): Promise<void> {
  const incomingBanner = page.locator('.incoming-call-banner');
  await expect(incomingBanner).toBeVisible();
  await incomingBanner.getByRole('button', { name: 'Accept' }).click();
}

async function waitForMeet(page: Page): Promise<void> {
  await expect(page).toHaveURL(/\/meet$/);
  await expect(page.locator('.meeting-stage')).toBeVisible();
}

async function forceCameraAndMicOff(page: Page): Promise<void> {
  const cameraOnButton = page.getByRole('button', { name: /^Camera On$/ });
  if (await cameraOnButton.isVisible()) {
    await cameraOnButton.click();
    await expect(page.getByRole('button', { name: /^Camera Off$/ })).toBeVisible();
  }

  const cameraOffButton = page.getByRole('button', { name: /^Camera Off$/ });
  if (await cameraOffButton.isVisible()) {
    await cameraOffButton.click();
  }

  const micOffActionButton = page.getByRole('button', { name: /^Mic Off$/ });
  if (await micOffActionButton.isVisible()) {
    await micOffActionButton.click();
  }

  await expect(page.getByRole('button', { name: /^Mic On$/ })).toBeVisible();
  await expect(page.getByRole('button', { name: /^Camera On$/ })).toBeVisible();
}

async function forceMicOn(page: Page): Promise<void> {
  const micOnActionButton = page.getByRole('button', { name: /^Mic On$/ });
  if (await micOnActionButton.isVisible()) {
    await micOnActionButton.click();
  }

  await expect(page.getByRole('button', { name: /^Mic Off$/ })).toBeVisible();
}

function remoteTileFor(page: Page, username: string) {
  return page.locator('.remote-tile', { hasText: username }).first();
}

async function expectRemoteTile(page: Page, username: string): Promise<void> {
  await expect(remoteTileFor(page, username)).toBeVisible({ timeout: 15_000 });
}

async function endCallIfInMeeting(page: Page): Promise<void> {
  const endCallButton = page.getByRole('button', { name: 'End Call' });
  if (await endCallButton.isVisible()) {
    await endCallButton.click();
    await expect(page).toHaveURL(/\/dashboard$/);
  }
}

test.describe.serial('Call Room Media Propagation', () => {
  test('mic on/off should propagate to all users in same room', async ({ browser }) => {
    const userA = makeUser('pw-mic-a');
    const userB = makeUser('pw-mic-b');
    const userC = makeUser('pw-mic-c');

    const contextA = await browser.newContext({ ignoreHTTPSErrors: true });
    const contextB = await browser.newContext({ ignoreHTTPSErrors: true });
    const contextC = await browser.newContext({ ignoreHTTPSErrors: true });

    const pageA = await contextA.newPage();
    const pageB = await contextB.newPage();
    const pageC = await contextC.newPage();

    await signupAndLogin(pageA, userA);
    await signupAndLogin(pageB, userB);
    await signupAndLogin(pageC, userC);

    await callUserBySearch(pageA, userB.username);
    await acceptIncomingCall(pageB);
    await waitForMeet(pageA);
    await waitForMeet(pageB);

    await callUserBySearch(pageA, userC.username);
    await acceptIncomingCall(pageC);
    await waitForMeet(pageC);

    const tileForAOnB = remoteTileFor(pageB, userA.username);
    const tileForAOnC = remoteTileFor(pageC, userA.username);

    await expectRemoteTile(pageB, userA.username);
    await expectRemoteTile(pageC, userA.username);

    await forceMicOn(pageA);
    await expect(tileForAOnB.locator('.remote-mic-status')).toContainText('Mic On');
    await expect(tileForAOnC.locator('.remote-mic-status')).toContainText('Mic On');

    await forceCameraAndMicOff(pageA);
    await expect(tileForAOnB.locator('.remote-mic-status')).toContainText('Mic Off');
    await expect(tileForAOnC.locator('.remote-mic-status')).toContainText('Mic Off');

    await contextA.close();
    await contextB.close();
    await contextC.close();
  });

  test('camera/mic off should be visible to all users in same room', async ({ browser }) => {
    const userA = makeUser('pw-a');
    const userB = makeUser('pw-b');
    const userC = makeUser('pw-c');

    const contextA = await browser.newContext({ ignoreHTTPSErrors: true });
    const contextB = await browser.newContext({ ignoreHTTPSErrors: true });
    const contextC = await browser.newContext({ ignoreHTTPSErrors: true });

    const pageA = await contextA.newPage();
    const pageB = await contextB.newPage();
    const pageC = await contextC.newPage();

    await signupAndLogin(pageA, userA);
    await signupAndLogin(pageB, userB);
    await signupAndLogin(pageC, userC);

    await callUserBySearch(pageA, userB.username);
    await acceptIncomingCall(pageB);
    await waitForMeet(pageA);
    await waitForMeet(pageB);

    await callUserBySearch(pageA, userC.username);
    await acceptIncomingCall(pageC);
    await waitForMeet(pageC);

    await forceCameraAndMicOff(pageA);

    const tileForAOnB = remoteTileFor(pageB, userA.username);
    const tileForAOnC = remoteTileFor(pageC, userA.username);

    await expectRemoteTile(pageB, userA.username);
    await expectRemoteTile(pageC, userA.username);

    await expect(tileForAOnB.locator('.remote-camera-off')).toBeVisible();
    await expect(tileForAOnB.locator('.remote-mic-status')).toContainText('Mic Off');

    await expect(tileForAOnC.locator('.remote-camera-off')).toBeVisible();
    await expect(tileForAOnC.locator('.remote-mic-status')).toContainText('Mic Off');

    await contextA.close();
    await contextB.close();
    await contextC.close();
  });

  test('users should reconnect and call again after ending call', async ({ browser }) => {
    const userA = makeUser('pw-re-a');
    const userB = makeUser('pw-re-b');

    const contextA = await browser.newContext({ ignoreHTTPSErrors: true });
    const contextB = await browser.newContext({ ignoreHTTPSErrors: true });

    const pageA = await contextA.newPage();
    const pageB = await contextB.newPage();

    await signupAndLogin(pageA, userA);
    await signupAndLogin(pageB, userB);

    await callUserBySearch(pageA, userB.username);
    await acceptIncomingCall(pageB);
    await waitForMeet(pageA);
    await waitForMeet(pageB);

    await endCallIfInMeeting(pageA);
    await pageB.goto('/dashboard');
    await expect(pageB).toHaveURL(/\/dashboard$/);

    await callUserBySearch(pageA, userB.username);
    await acceptIncomingCall(pageB);

    await waitForMeet(pageA);
    await waitForMeet(pageB);

    await contextA.close();
    await contextB.close();
  });
});
