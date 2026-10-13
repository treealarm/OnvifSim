import { createSocket } from 'node:dgram';
import { randomUUID } from 'node:crypto';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { discoveryPort } from './ports';

// The plan's acceptance check for the editor: a camera and a tree added in the browser show up as an
// ONVIF device on WS-Discovery and in the camera's picture.
test('a camera and a tree placed in the editor are live at once', async ({ page, request }) => {
  await openEditor(page);

  // A fixed camera, then aimed by hand: 3 m up at (0, 150), looking south with a narrow view, away
  // from the village and the person walking through it.
  await placeOnGround(page, 'Fixed camera');
  await expect(page.getByRole('heading', { level: 2 })).toContainText('cam3');
  await page.getByLabel('Name').fill('E2E camera');
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
    await page.getByRole('button', { name: /Village fixed/ }).click();
    const boxes = page.locator('.preview .seen');
    await expect(boxes.first()).toBeVisible();

    await page.getByLabel('Boxes on the picture').uncheck();
    await expect(boxes).toHaveCount(0);
  } finally {
    await request.delete(`/api/walkers/${standing.id}`);
  }
});

async function openEditor(page: Page) {
  await page.goto('/');
  await expect(page.getByText(/\d+ objects, .* cameras\./)).toBeVisible();
  // The viewport takes clicks once it has drawn a frame and seen the pointer move.
  await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await page.mouse.move(10, 200);
}

async function placeOnGround(page: Page, tool: string) {
  await page.getByRole('button', { name: tool, exact: true }).click();
  const viewport = page.locator('.viewport canvas');
  const box = await viewport.boundingBox();
  if (!box) throw new Error('The viewport is not on screen.');
  // The lower middle of the default view is open ground.
  await page.mouse.click(box.x + box.width * 0.5, box.y + box.height * 0.85);
}

async function setNumber(page: Page, label: string, value: number) {
  const field = page.getByLabel(label, { exact: true });
  await field.fill(String(value));
  await field.blur();
}

async function world(request: APIRequestContext) {
  return (await (await request.get('/api/world')).json()) as {
    cameras: { id: string; name: string; hFov: number }[];
    objects: { id: string; type: string; position: { x: number; z: number } }[];
    walkers: { id: string; route: unknown[]; return: string }[];
    terrain: { heights: string };
  };
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
