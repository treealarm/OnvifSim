import { createSocket } from 'node:dgram';
import { randomUUID } from 'node:crypto';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { discoveryPort } from './ports';

// Every test starts from the default world again, and test worlds do not pile up in the shared data
// directory. The recordings choice is always explicit, so no prompt can stop the cleanup.
test.afterEach(async ({ request }) => {
  const current = await worlds(request);
  if (current.active !== 'default') {
    await request.post('/api/worlds/default/activate', { data: { clearRecordings: false } });
  }
  const listing = await worlds(request);
  for (const name of listing.worlds) {
    if (name === 'default') continue;
    await request.post(`/api/worlds/${encodeURIComponent(name)}/trash`, { data: { clearRecordings: false } });
  }
  if ((await worlds(request)).trash.length > 0) await request.delete('/api/trash');
});

// The plan's acceptance check for the editor: a camera and a tree added in the browser show up as an
// ONVIF device on WS-Discovery and in the camera's picture.
test('a camera and a tree placed in the editor are live at once', async ({ page, request }) => {
  await openEditor(page);

  // A fixed camera, then aimed by hand: 3 m up at (0, 150), looking south with a narrow view, away
  // from the village and the person walking through it.
  await placeOnGround(page, 'Fixed camera');
  await expect(page.getByRole('heading', { level: 2 })).toContainText('cam3');
  await page.getByLabel('Name', { exact: true }).fill('E2E camera');
  await setNumber(page, 'X (east)', 0);
  await setNumber(page, 'Z (south)', 150);
  await setNumber(page, 'Above ground', 3);
  await setNumber(page, 'Heading', 180);
  await setNumber(page, 'Pitch', 0);
  await setNumber(page, 'Field of view', 30);
  await expect.poll(async () => (await cameraOf(request, 'cam3'))?.name).toBe('E2E camera');
  await expect.poll(async () => (await cameraOf(request, 'cam3'))?.hFov).toBe(30);
  const before = await snapshot(request, 'cam3');

  // A pine 20 m in front of it.
  await placeOnGround(page, 'Pine');
  await expect(page.getByRole('heading', { level: 2 })).toContainText('Pine');
  await setNumber(page, 'X (east)', 0);
  await setNumber(page, 'Z (south)', 170);
  await expect.poll(async () => (await objectAt(request, 0, 170))?.type).toBe('tree.pine');

  // The picture is deterministic, so the tree is the only thing that can have changed it.
  expect(Buffer.compare(await snapshot(request, 'cam3'), before)).not.toBe(0);

  const matches = await probe();
  expect(matches.some((m) => m.includes('onvif://www.onvif.org/name/E2E%20camera'))).toBe(true);
});

test('undo walks back through several edits and redo forward again', async ({ page, request }) => {
  await openEditor(page);

  await placeOnGround(page, 'Rock');
  await expect(page.getByRole('heading', { level: 2 })).toContainText('Rock');
  const id = (await page.getByRole('heading', { level: 2 }).locator('span').textContent()) ?? '';
  const placedX = (await objectById(request, id))?.position.x;
  // Edits of one field a second apart are separate steps.
  await setNumber(page, 'X (east)', 10);
  await expect.poll(async () => (await objectById(request, id))?.position.x).toBe(10);
  await page.waitForTimeout(1100);
  await setNumber(page, 'X (east)', 20);
  await expect.poll(async () => (await objectById(request, id))?.position.x).toBe(20);
  await page.keyboard.press('Delete');
  await expect.poll(async () => await objectById(request, id)).toBeUndefined();

  const undo = page.getByRole('button', { name: 'Undo' });
  const redo = page.getByRole('button', { name: 'Redo' });
  await undo.click();
  await expect.poll(async () => (await objectById(request, id))?.position.x).toBe(20);
  await undo.click();
  await expect.poll(async () => (await objectById(request, id))?.position.x).toBe(10);
  await undo.click();
  await expect.poll(async () => (await objectById(request, id))?.position.x).toBe(placedX);
  await page.keyboard.press('Control+z');
  await expect.poll(async () => await objectById(request, id)).toBeUndefined();
  await redo.click();
  await expect.poll(async () => (await objectById(request, id))?.position.x).toBe(placedX);
  await page.keyboard.press('Control+Shift+z');
  await expect.poll(async () => (await objectById(request, id))?.position.x).toBe(10);

  // A brush stroke is one step, undone to the very same heights.
  const heights = await terrainHeights(request);
  await openGroup(page, 'Terrain');
  await page.getByRole('button', { name: 'Raise', exact: true }).click();
  const box = await page.locator('.viewport canvas').boundingBox();
  if (!box) throw new Error('The viewport is not on screen.');
  await page.mouse.move(box.x + box.width * 0.3, box.y + box.height * 0.8);
  await page.mouse.down();
  for (let i = 1; i <= 10; i++) await page.mouse.move(box.x + box.width * 0.3 + i * 4, box.y + box.height * 0.8);
  await page.mouse.up();
  await expect.poll(() => terrainHeights(request)).not.toBe(heights);
  await undo.click();
  await expect.poll(() => terrainHeights(request)).toBe(heights);
});

test('a person drawn point by point walks the route', async ({ page, request }) => {
  await openEditor(page);
  const before = (await world(request)).walkers.length;

  await openGroup(page, 'Draw');
  await page.getByRole('button', { name: 'Person', exact: true }).click();
  const box = await page.locator('.viewport canvas').boundingBox();
  if (!box) throw new Error('The viewport is not on screen.');
  for (const [fx, fy] of [[0.3, 0.85], [0.45, 0.88], [0.4, 0.75]]) await page.mouse.click(box.x + box.width * fx, box.y + box.height * fy);
  await expect(page.getByText('3 points; double-click or Enter to finish')).toBeVisible();
  await page.keyboard.press('Enter');

  await expect(page.getByRole('heading', { level: 2 })).toContainText('Person');
  await expect.poll(async () => (await world(request)).walkers.length).toBe(before + 1);
  expect((await world(request)).walkers.at(-1)?.route).toHaveLength(3);
  await page.getByLabel('At the end').selectOption('reverse');
  await expect.poll(async () => (await world(request)).walkers.at(-1)?.return).toBe('reverse');
});

// Someone standing 15 m in front of the village camera is boxed over its picture, where the metadata
// stream puts them.
test('the camera picture boxes the people its analytics sees', async ({ page, request }) => {
  const standing = { id: 'e2e-standing', route: [{ x: 1, z: 45 }] };
  expect((await request.put(`/api/walkers/${standing.id}`, { data: standing })).ok()).toBe(true);
  try {
    await openEditor(page);
    await page.locator('.tree').getByRole('button', { name: /Village fixed/ }).click();
    const boxes = page.locator('.preview .seen');
    await expect(boxes.first()).toBeVisible();

    await page.getByLabel('Boxes on the picture').uncheck();
    await expect(boxes).toHaveCount(0);
  } finally {
    await request.delete(`/api/walkers/${standing.id}`);
  }
});

test('the toolbar groups tools and keeps the active tool marked', async ({ page }) => {
  await openEditor(page);
  const tabs = page.locator('.mode-tabs');

  await openGroup(page, 'Add');
  await page.getByRole('button', { name: 'Pine', exact: true }).click();
  await expect(tabs.getByRole('button', { name: 'Add', exact: true }).locator('.active-group-dot')).toBeVisible();

  // Switching the palette does not change the active tool; the dot stays on Add.
  await openGroup(page, 'Draw');
  await expect(tabs.getByRole('button', { name: 'Add', exact: true }).locator('.active-group-dot')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Person', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Pine', exact: true })).toHaveCount(0);

  await page.getByRole('button', { name: 'Person', exact: true }).click();
  await expect(tabs.getByRole('button', { name: 'Draw', exact: true }).locator('.active-group-dot')).toBeVisible();
  await page.keyboard.press('Escape');
});

test('the contents tree selects what it lists', async ({ page }) => {
  await openEditor(page);
  const tree = page.locator('.tree');

  await tree.getByRole('button', { name: /Village fixed/ }).click();
  await expect(page.getByRole('heading', { level: 2 })).toContainText('Village fixed');

  await tree.getByRole('button', { name: /Person w1/ }).click();
  await expect(page.getByRole('heading', { level: 2 })).toContainText('Person');

  const houses = tree.locator('details.tree-type', { has: page.locator('summary', { hasText: /^House/ }) });
  await houses.locator('summary').click();
  await houses.getByRole('button').first().click();
  await expect(page.getByRole('heading', { level: 2 })).toContainText('House');

  await tree.getByRole('button', { name: 'World environment' }).click();
  await expect(page.getByRole('heading', { level: 2 })).toHaveText('World');
});

test('a person can be selected by clicking it in the viewport', async ({ page, request }) => {
  // The village walkers would walk through the middle of the picture; keep them out of the way and
  // put them back afterwards, so the click can only hit the person standing at the origin.
  const before = (await (await request.get('/api/world')).json()) as { walkers: { id: string }[] };
  for (const walker of before.walkers) await request.delete(`/api/walkers/${walker.id}`);
  const standing = { id: 'e2e-click', route: [{ x: 0, z: 0 }] };
  expect((await request.put(`/api/walkers/${standing.id}`, { data: standing })).ok()).toBe(true);
  try {
    await openEditor(page);
    const box = await page.locator('.viewport canvas').boundingBox();
    if (!box) throw new Error('The viewport is not on screen.');
    // The default camera looks at the origin, where the person stands.
    await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
    await expect(page.getByRole('heading', { level: 2 })).toContainText('Person');
    await expect(page.getByRole('heading', { level: 2 })).toContainText(standing.id);
  } finally {
    await request.delete(`/api/walkers/${standing.id}`);
    for (const walker of before.walkers) await request.put(`/api/walkers/${walker.id}`, { data: walker });
  }
});

test('a car can be placed and its plate edited', async ({ page, request }) => {
  await openEditor(page);
  await placeOnGround(page, 'Car');
  const heading = page.getByRole('heading', { level: 2 });
  await expect(heading).toContainText('Car');
  const id = (await heading.locator('span').textContent()) ?? '';
  await expect.poll(async () => (await objectById(request, id))?.type).toBe('car');
  await expect.poll(async () => (await objectById(request, id))?.plate).toBe('A123BC');

  await page.getByLabel('Plate text', { exact: true }).fill('B777AA');
  await page.getByLabel('Plate text', { exact: true }).blur();
  await expect.poll(async () => (await objectById(request, id))?.plate).toBe('B777AA');
});


test('a camera stream can be added, given a path and removed', async ({ page, request }) => {
  await openEditor(page);
  await placeOnGround(page, 'Fixed camera');
  const heading = page.getByRole('heading', { level: 2 });
  await expect(heading).toContainText(/cam\d+/);
  const id = (await heading.locator('span').textContent()) ?? '';
  try {
    await page.getByRole('button', { name: 'Add JPEG stream', exact: true }).click();
    const card = page.locator('.stream-card').filter({ hasText: 'Profile_3' });
    await expect(card).toBeVisible();
    await card.getByLabel('RTSP path', { exact: true }).fill(`${id}/second`);
    await card.getByLabel('RTSP path', { exact: true }).blur();
    await expect.poll(async () => (await cameraById(request, id))?.streams?.length).toBe(3);
    await expect.poll(async () => (await cameraById(request, id))?.streams?.at(-1)?.path).toBe(`${id}/second`);

    await card.getByRole('button', { name: 'Remove', exact: true }).click();
    await expect.poll(async () => (await cameraById(request, id))?.streams?.length).toBe(2);
  } finally {
    await request.delete(`/api/cameras/${id}`);
  }
});

test('a camera listens where the inspector says', async ({ page, request }) => {
  await openEditor(page);
  await placeOnGround(page, 'Fixed camera');
  const heading = page.getByRole('heading', { level: 2 });
  await expect(heading).toContainText(/cam\d+/);
  const id = (await heading.locator('span').textContent()) ?? '';
  const port = 20000 + Math.floor(Math.random() * 20000);
  try {
    await page.getByLabel('Bind address (empty: any)', { exact: true }).fill('127.0.0.1');
    await page.getByLabel('Bind address (empty: any)', { exact: true }).blur();
    await page.getByLabel('RTSP port (0: default)', { exact: true }).fill(String(port));
    await page.getByLabel('RTSP port (0: default)', { exact: true }).blur();
    await expect.poll(async () => (await cameraById(request, id))?.bindAddress).toBe('127.0.0.1');
    await expect.poll(async () => (await cameraById(request, id))?.rtspPort).toBe(port);
  } finally {
    await request.delete(`/api/cameras/${id}`);
  }
});

test('creating and switching a world clears the edit history', async ({ page, request }) => {
  await openEditor(page);
  await placeOnGround(page, 'Rock');
  await expect(page.getByRole('button', { name: 'Undo' })).toBeEnabled();

  const name = `e2e-world-${Date.now()}`;
  await createWorld(page, name, true);
  await expect(page.getByText('0 objects, 0 roads, 0 people, 0 cameras.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Undo' })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Redo' })).toBeDisabled();
  expect((await worlds(request)).active).toBe(name);

  // Undo starts again in the new world.
  await placeOnGround(page, 'Rock');
  await expect(page.getByRole('button', { name: 'Undo' })).toBeEnabled();
  await page.getByRole('button', { name: 'Undo' }).click();
  await expect.poll(async () => (await world(request)).objects.length).toBe(0);

  // An empty world has no recordings, so the next switch asks nothing.
  const second = `${name}-2`;
  await createWorld(page, second, false);
  expect((await worlds(request)).active).toBe(second);
  await expect(page.getByRole('dialog', { name: 'Camera recordings' })).toHaveCount(0);
});

test('a world can be trashed, restored and the trash emptied', async ({ page, request }) => {
  await openEditor(page);
  const name = `e2e-trash-${Date.now()}`;
  await createWorld(page, name, true);
  // Back to the village; the empty test world has no recordings, so there is no question.
  await openWorld(page, 'default', false);
  await expect.poll(async () => (await worlds(request)).active).toBe('default');

  await page.getByRole('button', { name: /^World:/ }).click();
  const manager = page.getByRole('dialog', { name: 'Worlds' });
  await manager.locator('.world-row').filter({ hasText: name }).first().getByRole('button', { name: 'Trash', exact: true }).click();
  await expect.poll(async () => (await worlds(request)).trash).toContain(name);

  await manager.locator('.world-row').filter({ hasText: name }).getByRole('button', { name: 'Restore', exact: true }).click();
  await expect.poll(async () => (await worlds(request)).worlds).toContain(name);

  await manager.locator('.world-row').filter({ hasText: name }).first().getByRole('button', { name: 'Trash', exact: true }).click();
  await expect.poll(async () => (await worlds(request)).trash).toContain(name);

  await manager.getByRole('button', { name: 'Empty trash', exact: true }).click();
  const confirm = page.getByRole('dialog', { name: 'Empty trash' });
  await confirm.getByRole('button', { name: 'Empty trash', exact: true }).click();
  await expect.poll(async () => (await worlds(request)).trash.length).toBe(0);
});

test('trashing the active world opens another one', async ({ page, request }) => {
  await openEditor(page);
  const name = `e2e-active-${Date.now()}`;
  await createWorld(page, name, true);

  await page.getByRole('button', { name: /^World:/ }).click();
  const manager = page.getByRole('dialog', { name: 'Worlds' });
  await manager.locator('.world-row').filter({ hasText: name }).first().getByRole('button', { name: 'Trash', exact: true }).click();
  await expect.poll(async () => (await worlds(request)).active).toBe('default');
  expect((await worlds(request)).trash).toContain(name);
});

async function openEditor(page: Page) {
  await page.goto('/');
  await expect(page.getByText(/\d+ objects, .* cameras\./)).toBeVisible();
  // The viewport takes clicks once it has drawn a frame and seen the pointer move.
  await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await page.mouse.move(10, 200);
}

async function openGroup(page: Page, group: 'Select' | 'Add' | 'Draw' | 'Terrain') {
  await page.locator('.mode-tabs').getByRole('button', { name: group, exact: true }).click();
}

async function placeOnGround(page: Page, tool: string) {
  await openGroup(page, 'Add');
  await page.getByRole('button', { name: tool, exact: true }).click();
  const viewport = page.locator('.viewport canvas');
  const box = await viewport.boundingBox();
  if (!box) throw new Error('The viewport is not on screen.');
  // The lower middle of the default view is open ground.
  await page.mouse.click(box.x + box.width * 0.5, box.y + box.height * 0.85);
}

// Creates an empty world and opens it. When the outgoing world has cameras, the server asks about the
// recordings before anything is written.
async function createWorld(page: Page, name: string, expectRecordings: boolean) {
  await page.getByRole('button', { name: /^World:/ }).click();
  const manager = page.getByRole('dialog', { name: 'Worlds' });
  await expect(manager).toBeVisible();
  await manager.getByLabel('New world').fill(name);
  await manager.getByRole('button', { name: 'Create', exact: true }).click();
  await answerRecordings(page, expectRecordings);
  await expect(manager).toBeHidden();
}

async function openWorld(page: Page, name: string, expectRecordings: boolean) {
  await page.getByRole('button', { name: /^World:/ }).click();
  const manager = page.getByRole('dialog', { name: 'Worlds' });
  await manager.locator('.world-row').filter({ hasText: name }).first().locator('button.name').click();
  await answerRecordings(page, expectRecordings);
  await expect(manager).toBeHidden();
}

async function answerRecordings(page: Page, expected: boolean) {
  const prompt = page.getByRole('dialog', { name: 'Camera recordings' });
  if (expected) {
    await expect(prompt).toBeVisible();
    await prompt.getByRole('button', { name: 'Keep recordings' }).click();
  } else {
    await expect(prompt).toHaveCount(0);
  }
}

async function setNumber(page: Page, label: string, value: number) {
  const field = page.getByLabel(label, { exact: true });
  await field.fill(String(value));
  await field.blur();
}

interface WorldsResponse {
  active: string;
  worlds: string[];
  trash: string[];
  recordings: { hasRecordings: boolean; cameraCount: number };
}

async function worlds(request: APIRequestContext): Promise<WorldsResponse> {
  return (await (await request.get('/api/worlds')).json()) as WorldsResponse;
}

async function world(request: APIRequestContext) {
  return (await (await request.get('/api/world')).json()) as {
    cameras: { id: string; name: string; hFov: number; bindAddress?: string | null; rtspPort?: number; streams?: { token: string; path: string }[] }[];
    objects: { id: string; type: string; plate?: string | null; position: { x: number; z: number } }[];
    walkers: { id: string; route: unknown[]; return: string }[];
    terrain: { heights: string };
  };
}

async function cameraById(request: APIRequestContext, id: string) {
  return (await world(request)).cameras.find((c) => c.id === id);
}

async function cameraOf(request: APIRequestContext, id: string) {
  return (await world(request)).cameras.find((c) => c.id === id);
}

async function objectAt(request: APIRequestContext, x: number, z: number) {
  return (await world(request)).objects.find((o) => o.position.x === x && o.position.z === z);
}

async function objectById(request: APIRequestContext, id: string) {
  return (await world(request)).objects.find((o) => o.id === id);
}

async function terrainHeights(request: APIRequestContext): Promise<string> {
  return (await world(request)).terrain.heights;
}

async function snapshot(request: APIRequestContext, id: string): Promise<Buffer> {
  const response = await request.get(`/api/cameras/${id}/snapshot.jpg`);
  expect(response.ok()).toBe(true);
  return await response.body();
}

// A WS-Discovery Probe sent straight to the simulator's discovery port; every camera answers with a
// ProbeMatches message of its own.
async function probe(): Promise<string[]> {
  const socket = createSocket('udp4');
  const matches: string[] = [];
  socket.on('message', (message) => matches.push(message.toString()));
  const envelope =
    '<?xml version="1.0" encoding="UTF-8"?>' +
    '<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery" xmlns:dn="http://www.onvif.org/ver10/network/wsdl">' +
    `<s:Header><a:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</a:Action><a:MessageID>uuid:${randomUUID()}</a:MessageID>` +
    '<a:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</a:To></s:Header>' +
    '<s:Body><d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe></s:Body></s:Envelope>';
  await new Promise<void>((resolve, reject) => socket.send(envelope, discoveryPort, '127.0.0.1', (e) => (e ? reject(e) : resolve())));
  await new Promise((resolve) => setTimeout(resolve, 1500));
  socket.close();
  return matches;
}
