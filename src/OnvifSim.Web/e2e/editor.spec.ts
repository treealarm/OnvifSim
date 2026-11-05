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
  const placed = (await page.getByRole('heading', { level: 2 }).locator('span').textContent()) ?? '';
  await page.getByLabel('Name', { exact: true }).fill('E2E camera');
  await setNumber(page, 'X (east)', 0);
  await setNumber(page, 'Z (south)', 150);
  await setNumber(page, 'Above ground', 3);
  await setNumber(page, 'Heading', 180);
  await setNumber(page, 'Pitch', 0);
  await setNumber(page, 'Field of view', 30);
  await expect.poll(async () => (await cameraOf(request, placed))?.name).toBe('E2E camera');
  await expect.poll(async () => (await cameraOf(request, placed))?.hFov).toBe(30);
  const before = await snapshot(request, placed);

  // A pine 20 m in front of it.
  await placeOnGround(page, 'Pine');
  await expect(page.getByRole('heading', { level: 2 })).toContainText('Pine');
  await setNumber(page, 'X (east)', 0);
  await setNumber(page, 'Z (south)', 170);
  await expect.poll(async () => (await objectAt(request, 0, 170))?.type).toBe('tree.pine');

  // The picture is deterministic, so the tree is the only thing that can have changed it.
  expect(Buffer.compare(await snapshot(request, placed), before)).not.toBe(0);

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
  await page.getByLabel('Closed loop', { exact: true }).uncheck();
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
    // The default camera looks at the origin, where the person stands. A slow machine may still be
    // mounting the scene when the page first appears, and a click then lands on bare ground, so keep
    // clicking until the person takes the selection.
    const heading = page.getByRole('heading', { level: 2 });
    await expect
      .poll(
        async () => {
          await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
          return (await heading.textContent()) ?? '';
        },
        { timeout: 15_000 },
      )
      .toContain(standing.id);
    await expect(heading).toContainText('Person');
  } finally {
    await request.delete(`/api/walkers/${standing.id}`);
    for (const walker of before.walkers) await request.put(`/api/walkers/${walker.id}`, { data: walker });
  }
});

test('a road can be closed into a ring', async ({ page, request }) => {
  await openEditor(page);
  await openGroup(page, 'Draw');
  await page.getByRole('button', { name: 'Road', exact: true }).click();
  const box = await page.locator('.viewport canvas').boundingBox();
  if (!box) throw new Error('The viewport is not on screen.');
  for (const [fx, fy] of [[0.25, 0.72], [0.5, 0.72], [0.5, 0.82], [0.25, 0.82]]) await page.mouse.click(box.x + box.width * fx, box.y + box.height * fy);
  await expect(page.getByText('4 points; double-click or Enter to finish')).toBeVisible();
  await page.keyboard.press('Enter');

  const heading = page.getByRole('heading', { level: 2 });
  await expect(heading).toContainText('Road');
  const id = (await heading.locator('span').textContent()) ?? '';
  try {
    await page.getByLabel('Closed loop', { exact: true }).check();
    await expect.poll(async () => (await world(request)).roads.find((r) => r.id === id)?.closed).toBe(true);
    await page.getByLabel('Closed loop', { exact: true }).uncheck();
    await expect.poll(async () => (await world(request)).roads.find((r) => r.id === id)?.closed).toBe(false);
  } finally {
    await request.delete(`/api/roads/${id}`);
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

  // The parked car becomes a moving one, keeping its plate; one Undo parks it again.
  await page.getByRole('button', { name: 'Make it drive', exact: true }).click();
  await expect(page.getByRole('heading', { level: 2 })).toContainText('Car');
  await expect.poll(async () => (await world(request)).objects.some((o) => o.id === id)).toBe(false);
  await expect.poll(async () => (await world(request)).vehicles.find((v) => v.plate === 'B777AA')?.route.length).toBe(2);

  await page.getByRole('button', { name: 'Undo', exact: true }).click();
  await expect.poll(async () => (await world(request)).objects.some((o) => o.id === id)).toBe(true);
  await expect.poll(async () => (await world(request)).vehicles.some((v) => v.plate === 'B777AA')).toBe(false);
});

test('a car drives the route drawn for it', async ({ page, request }) => {
  await openEditor(page);
  await openGroup(page, 'Draw');
  await page.getByRole('button', { name: 'Car route', exact: true }).click();
  const box = await page.locator('.viewport canvas').boundingBox();
  if (!box) throw new Error('The viewport is not on screen.');
  for (const [fx, fy] of [[0.25, 0.7], [0.5, 0.72], [0.75, 0.7]]) await page.mouse.click(box.x + box.width * fx, box.y + box.height * fy);
  await expect(page.getByText('3 points; double-click or Enter to finish')).toBeVisible();
  await page.keyboard.press('Enter');

  const heading = page.getByRole('heading', { level: 2 });
  await expect(heading).toContainText('Car');
  const id = (await heading.locator('span').textContent()) ?? '';
  try {
    await expect.poll(async () => (await world(request)).vehicles.find((v) => v.id === id)?.route.length).toBe(3);

    await page.getByLabel('Plate text', { exact: true }).fill('B777AA');
    await page.getByLabel('Plate text', { exact: true }).blur();
    await expect.poll(async () => (await world(request)).vehicles.find((v) => v.id === id)?.plate).toBe('B777AA');

    // The route is travelled on the clock, so the car is somewhere else a moment later.
    const left = (await (await request.get(`/api/vehicles/${id}/pose`)).json()) as { x: number; z: number };
    await page.waitForTimeout(600);
    const right = (await (await request.get(`/api/vehicles/${id}/pose`)).json()) as { x: number; z: number };
    expect([right.x, right.z]).not.toEqual([left.x, left.z]);
  } finally {
    await request.delete(`/api/vehicles/${id}`);
  }
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
  await expect(page.getByText('0 objects, 0 roads, 0 people, 0 cameras, 0 lamps.')).toBeVisible();
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

  await page.getByRole('button', { name: 'Manage worlds', exact: true }).click();
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

  await page.getByRole('button', { name: 'Manage worlds', exact: true }).click();
  const manager = page.getByRole('dialog', { name: 'Worlds' });
  await manager.locator('.world-row').filter({ hasText: name }).first().getByRole('button', { name: 'Trash', exact: true }).click();
  await expect.poll(async () => (await worlds(request)).active).toBe('default');
  expect((await worlds(request)).trash).toContain(name);
});

// A lamp is a device of the world, not a part of a camera, and it is put down from the palette like
// any object. A relay of a camera drives it, and the same relay may drive a sensor of its own camera:
// closed, the input reads 0 and the lamp is lit.
test('a lamp is placed from the palette and a relay lights it', async ({ page, request }) => {
  await openEditor(page);

  await placeOnGround(page, 'Lamp');
  await expect(page.getByRole('heading', { level: 2 })).toContainText('lamp1');
  await page.getByLabel('Name', { exact: true }).fill('E2E lamp');
  await page.getByLabel('Name', { exact: true }).blur();
  await expect.poll(async () => (await world(request)).lamps?.find((l) => l.id === 'lamp1')?.name).toBe('E2E lamp');
  // Contents lists it beside the other things of the world.
  await expect(page.locator('.tree').getByRole('button', { name: /E2E lamp/ })).toBeVisible();

  await placeOnGround(page, 'Fixed camera');
  await expect(page.getByRole('heading', { level: 2 })).toContainText(/cam\d+/);
  const cameraId = (await page.getByRole('heading', { level: 2 }).locator('span').textContent()) ?? '';
  await page.getByRole('button', { name: 'Add relay output', exact: true }).click();
  await page.getByRole('button', { name: 'Add digital input', exact: true }).click();
  await expect.poll(async () => (await cameraById(request, cameraId))?.relayOutputs?.length).toBe(1);
  await expect.poll(async () => (await cameraById(request, cameraId))?.digitalInputs?.length).toBe(1);

  // The relay drives a sensor of the camera it belongs to.
  await page.getByLabel('Wired to', { exact: true }).selectOption({ label: `Fixed ${cameraId} / Input input1 (input)` });
  const sensor = page.locator('.io-card').filter({ has: page.locator('strong', { hasText: 'Input input1' }) });
  await expect(sensor).toContainText('1');
  // The states are a caption on the picture, where a device of a camera is shown.
  await expect(page.locator('.preview-devices')).toContainText('Input input1: 1');
  // A normally-open relay closes its contact when energised; the card says both things apart.
  const relayCard = page.locator('.io-card').filter({ has: page.getByLabel('Energised (active)') });
  await expect(relayCard).toContainText('Contact: open');
  await page.getByLabel('Energised (active)', { exact: true }).check();
  await expect(relayCard).toContainText('Contact: closed');
  await expect(sensor).toContainText('0');
  await expect(page.locator('.preview-devices')).toContainText('Input input1: 0');
  await expect.poll(async () => (await world(request)).wires?.some(
    (w) => w.relayCameraId === cameraId && w.inputCameraId === cameraId && w.inputToken === 'input1')).toBe(true);

  // The same relay lights the lamp instead, and the lamp is lit while it stays closed.
  await page.getByLabel('Wired to', { exact: true }).selectOption({ label: 'E2E lamp (lamp)' });
  await expect.poll(async () => (await world(request)).wires?.some(
    (w) => w.relayCameraId === cameraId && w.lampId === 'lamp1')).toBe(true);
  // A lamp is edited like a camera: its own card, found in Contents.
  await page.locator('.tree').getByRole('button', { name: /E2E lamp/ }).click();
  await expect(page.getByRole('heading', { level: 2 })).toContainText('E2E lamp');
  await expect(page.locator('.inspector .io-card').filter({ hasText: 'E2E lamp' })).toContainText('lit');
});

// The wiring window is an action of the whole editor beside the connection state, not a tool of the
// palette: it connects a sink of any camera or lamp to relays of any camera.
test('the wiring window connects lamps and sensors to relays', async ({ page, request }) => {
  await openEditor(page);
  await page.getByRole('button', { name: 'Wiring…', exact: true }).click();
  const wiring = page.getByRole('dialog', { name: 'Wiring' });
  await expect(wiring).toBeVisible();

  // The default world's relay lights the gate lamp and holds the tower's sensor open.
  await wiring.getByRole('button', { name: /Gate lamp/ }).click();
  const relay = wiring.getByRole('checkbox', { name: /Village fixed \/ Village lamp/ });
  await expect(relay).toBeChecked();
  await relay.uncheck();
  await expect.poll(async () => (await world(request)).wires?.some((w) => w.lampId === 'gate')).toBe(false);
  await relay.check();
  await expect.poll(async () => (await world(request)).wires?.some((w) => w.lampId === 'gate')).toBe(true);
});

async function openEditor(page: Page) {
  await page.goto('/');
  await expect(page.getByText(/\d+ objects, .* cameras, \d+ lamps\./)).toBeVisible();
  // A canvas is 300x150 until it takes the viewport's size a frame later; coordinates measured
  // before that fall in the wrong part of the picture, so wait until it fills its container.
  const canvas = page.locator('.viewport canvas');
  await expect
    .poll(async () => {
      const viewport = await page.locator('.viewport').boundingBox();
      const drawn = await canvas.boundingBox();
      return viewport !== null && drawn !== null && Math.abs(drawn.width - viewport.width) < 1 && Math.abs(drawn.height - viewport.height) < 1;
    })
    .toBe(true);
  // The viewport takes clicks once it has drawn a frame and seen the pointer move.
  await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  const box = await canvas.boundingBox();
  if (!box) throw new Error('The viewport is not on screen.');
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
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
  await page.getByRole('button', { name: 'Manage worlds', exact: true }).click();
  const manager = page.getByRole('dialog', { name: 'Worlds' });
  await expect(manager).toBeVisible();
  await manager.getByLabel('New world').fill(name);
  await manager.getByRole('button', { name: 'Create', exact: true }).click();
  await answerRecordings(page, expectRecordings);
  await expect(manager).toBeHidden();
}

async function openWorld(page: Page, name: string, expectRecordings: boolean) {
  await page.getByLabel('World', { exact: true }).selectOption(name);
  await answerRecordings(page, expectRecordings);
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
    cameras: {
      id: string; name: string; hFov: number; bindAddress?: string | null; rtspPort?: number;
      streams?: { token: string; path: string }[];
      relayOutputs?: { token: string }[]; digitalInputs?: { token: string }[];
    }[];
    objects: { id: string; type: string; plate?: string | null; position: { x: number; z: number } }[];
    roads: { id: string; closed: boolean }[];
    walkers: { id: string; route: unknown[]; return: string }[];
    vehicles: { id: string; route: unknown[]; plate?: string | null }[];
    lamps?: { id: string; name: string; normallyClosed: boolean }[];
    wires?: { relayCameraId: string; relayToken: string; inputCameraId?: string | null; inputToken?: string | null; lampId?: string | null }[];
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
