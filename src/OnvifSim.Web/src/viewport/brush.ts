import type { BrushKind } from '../store';
import type { Terrain } from '../model';

export interface BrushDab {
  kind: BrushKind;
  x: number;
  z: number;
  radius: number;
  // Metres per dab for raise and lower; the fraction of the way to the target for smooth and flatten.
  strength: number;
  // The height flatten pulls towards: where the stroke started.
  flattenTo: number;
}

// One dab of a terrain brush at (x, z), changing heights in place with a smooth falloff to the edge.
export function applyBrush(terrain: Terrain, heights: Float32Array, dab: BrushDab) {
  const { resolution, size } = terrain;
  const cell = size / resolution;
  const row = resolution + 1;
  const toIndex = (v: number) => (v + size / 2) / cell;
  const fromX = Math.max(0, Math.floor(toIndex(dab.x - dab.radius)));
  const toX = Math.min(resolution, Math.ceil(toIndex(dab.x + dab.radius)));
  const fromZ = Math.max(0, Math.floor(toIndex(dab.z - dab.radius)));
  const toZ = Math.min(resolution, Math.ceil(toIndex(dab.z + dab.radius)));
  // Smooth averages the heights as they were before this dab, so the result does not depend on the
  // order the samples are visited in.
  const before = dab.kind === 'smooth' ? heights.slice() : heights;
  const at = (ix: number, iz: number) => before[Math.min(resolution, Math.max(0, iz)) * row + Math.min(resolution, Math.max(0, ix))];

  for (let iz = fromZ; iz <= toZ; iz++) {
    for (let ix = fromX; ix <= toX; ix++) {
      const distance = Math.hypot(ix * cell - size / 2 - dab.x, iz * cell - size / 2 - dab.z);
      if (distance > dab.radius) continue;
      const falloff = (1 - (distance / dab.radius) ** 2) ** 2;
      const i = iz * row + ix;
      heights[i] += change(dab, heights[i], falloff, () => (at(ix - 1, iz) + at(ix + 1, iz) + at(ix, iz - 1) + at(ix, iz + 1)) / 4);
    }
  }
}

function change(dab: BrushDab, height: number, falloff: number, neighbourAverage: () => number): number {
  const pull = Math.min(1, dab.strength) * falloff;
  switch (dab.kind) {
    case 'raise':
      return dab.strength * falloff;
    case 'lower':
      return -dab.strength * falloff;
    case 'smooth':
      return (neighbourAverage() - height) * pull;
    case 'flatten':
      return (dab.flattenTo - height) * pull;
  }
}
