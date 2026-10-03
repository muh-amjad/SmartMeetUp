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

/** From the dashboard: find someone and press Call, which opens a new meeting page ringing them. */
async function callFromDashboard(callerPage: Page, targetUsername: string): Promise<void> {
  await callerPage.getByPlaceholder('Username or email').fill(targetUsername);
  await callerPage.getByRole('button', { name: /^Find$/ }).click();

  const searchItem = callerPage.locator('.search-item', { hasText: targetUsername }).first();
  await expect(searchItem).toBeVisible();
  await searchItem.getByRole('button', { name: /^Call$/ }).click();
}

/** From inside a call: the only way to bring someone in is "Add people". */
async function addToCall(page: Page, targetUsername: string): Promise<void> {
  const panel = page.getByRole('complementary', { name: 'People' });
  if (!(await panel.isVisible())) {
    await page.getByRole('button', { name: 'Add people' }).last().click();
  }
  await panel.getByRole('button', { name: `Add ${targetUsername} to the call` }).click();
}

function incomingCall(page: Page) {
  return page.getByRole('alertdialog');
}

async function acceptIncomingCall(page: Page): Promise<void> {
  await expect(incomingCall(page)).toBeVisible();
  await incomingCall(page).getByRole('button', { name: 'Accept' }).click();
  await expect(incomingCall(page)).toBeHidden();
}

async function waitForMeet(page: Page): Promise<void> {
  await expect(page).toHaveURL(/\/meet\/[0-9a-f-]{36}$/);
  await expect(page.locator('.meeting-stage')).toBeVisible();
}

async function forceCameraAndMicOff(page: Page): Promise<void> {
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

async function endCall(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'End Call' }).click();
  await expect(page).toHaveURL(/\/dashboard$/);
}

test.describe.serial('Calling', () => {
  test('an incoming call rings as a popup on any page, and stops ringing when answered', async ({ browser }) => {
    const userA = makeUser('pw-ring-a');
    const userB = makeUser('pw-ring-b');

    const contextA = await browser.newContext({ ignoreHTTPSErrors: true });
    const contextB = await browser.newContext({ ignoreHTTPSErrors: true });
    const pageA = await contextA.newPage();
    const pageB = await contextB.newPage();

    await signupAndLogin(pageA, userA);
    await signupAndLogin(pageB, userB);

    // Not the dashboard: calls used to reach only the dashboard and the call page.
    await pageB.getByRole('link', { name: 'Meetings' }).first().click();
    await expect(pageB).toHaveURL(/\/meetings$/);

    await callFromDashboard(pageA, userB.username);
    await waitForMeet(pageA);
    await expect(pageA.getByText(`Calling ${userB.username}…`)).toBeVisible();

    const popup = incomingCall(pageB);
    await expect(popup).toBeVisible();
    await expect(popup).toContainText(userA.username);
    await expect(popup).toHaveAttribute('data-ringing', 'true');

    await popup.getByRole('button', { name: 'Accept' }).click();
    await expect(popup).toBeHidden();
    await waitForMeet(pageB);
    await expect(pageB).toHaveURL(pageA.url());

    await expectRemoteTile(pageA, userB.username);
    await expectRemoteTile(pageB, userA.username);

    await contextA.close();
    await contextB.close();
  });

  test('declining hangs up the caller and returns them to the dashboard', async ({ browser }) => {
    const userA = makeUser('pw-dec-a');
    const userB = makeUser('pw-dec-b');

    const contextA = await browser.newContext({ ignoreHTTPSErrors: true });
    const contextB = await browser.newContext({ ignoreHTTPSErrors: true });
    const pageA = await contextA.newPage();
    const pageB = await contextB.newPage();

    await signupAndLogin(pageA, userA);
    await signupAndLogin(pageB, userB);

    await callFromDashboard(pageA, userB.username);
    await waitForMeet(pageA);

    await incomingCall(pageB).getByRole('button', { name: 'Decline' }).click();
    await expect(incomingCall(pageB)).toBeHidden();

    await expect(pageA).toHaveURL(/\/dashboard$/);
    await expect(pageA.getByText(`${userB.username} declined the call.`)).toBeVisible();

    await contextA.close();
    await contextB.close();
  });

  test('the callee stops ringing when the caller hangs up first', async ({ browser }) => {
    const userA = makeUser('pw-hang-a');
    const userB = makeUser('pw-hang-b');

    const contextA = await browser.newContext({ ignoreHTTPSErrors: true });
    const contextB = await browser.newContext({ ignoreHTTPSErrors: true });
    const pageA = await contextA.newPage();
    const pageB = await contextB.newPage();

    await signupAndLogin(pageA, userA);
    await signupAndLogin(pageB, userB);

    await callFromDashboard(pageA, userB.username);
    await waitForMeet(pageA);
    await expect(incomingCall(pageB)).toBeVisible();

    await endCall(pageA);

    await expect(incomingCall(pageB)).toBeHidden();
    await expect(pageB.getByText(`Missed call from ${userA.username}.`)).toBeVisible();

    await contextA.close();
    await contextB.close();
  });

  test('the meeting page has no lobby: /meet on its own goes to the dashboard', async ({ browser }) => {
    const user = makeUser('pw-lobby');
    const context = await browser.newContext({ ignoreHTTPSErrors: true });
    const page = await context.newPage();

    await signupAndLogin(page, user);
    await page.goto('/meet');
    await expect(page).toHaveURL(/\/dashboard$/);

    await context.close();
  });
});

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

    await callFromDashboard(pageA, userB.username);
    await acceptIncomingCall(pageB);
    await waitForMeet(pageA);
    await waitForMeet(pageB);

    await addToCall(pageA, userC.username);
    await acceptIncomingCall(pageC);
    await waitForMeet(pageC);
    await expect(pageC).toHaveURL(pageA.url());

    const tileForAOnB = remoteTileFor(pageB, userA.username);
    const tileForAOnC = remoteTileFor(pageC, userA.username);

    await expectRemoteTile(pageB, userA.username);
    await expectRemoteTile(pageC, userA.username);

    await forceMicOn(pageA);
    await expect(tileForAOnB.locator('.mic-badge')).toBeHidden();
    await expect(tileForAOnC.locator('.mic-badge')).toBeHidden();

    await forceCameraAndMicOff(pageA);
    await expect(tileForAOnB.locator('.mic-badge')).toHaveText('Muted');
    await expect(tileForAOnC.locator('.mic-badge')).toHaveText('Muted');

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

    await callFromDashboard(pageA, userB.username);
    await acceptIncomingCall(pageB);
    await waitForMeet(pageA);
    await waitForMeet(pageB);

    await addToCall(pageA, userC.username);
    await acceptIncomingCall(pageC);
    await waitForMeet(pageC);

    await forceCameraAndMicOff(pageA);

    const tileForAOnB = remoteTileFor(pageB, userA.username);
    const tileForAOnC = remoteTileFor(pageC, userA.username);

    await expectRemoteTile(pageB, userA.username);
    await expectRemoteTile(pageC, userA.username);

    await expect(tileForAOnB.locator('.tile-placeholder')).toBeVisible();
    await expect(tileForAOnB.locator('.mic-badge')).toHaveText('Muted');

    await expect(tileForAOnC.locator('.tile-placeholder')).toBeVisible();
    await expect(tileForAOnC.locator('.mic-badge')).toHaveText('Muted');

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

    await callFromDashboard(pageA, userB.username);
    await acceptIncomingCall(pageB);
    await waitForMeet(pageA);
    await waitForMeet(pageB);

    await endCall(pageA);
    await endCall(pageB);

    await callFromDashboard(pageA, userB.username);
    await acceptIncomingCall(pageB);

    await waitForMeet(pageA);
    await waitForMeet(pageB);

    await contextA.close();
    await contextB.close();
  });
});
