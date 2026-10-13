import { useThree, type ThreeEvent } from '@react-three/fiber';
import { useEffect, useMemo, useRef } from 'react';
import { BufferAttribute, BufferGeometry, Color, type Mesh } from 'three';
import { heightAt, type Terrain } from '../model';
import { place, select, updateTerrain, useEditor } from '../store';
import { applyBrush, type BrushDab } from './brush';

interface Stroke {
  heights: Float32Array;
  flattenTo: number;
}

// The ground: clicked to place things or to clear the selection, painted on with the terrain brushes.
// A stroke reshapes a copy of the heights straight in the geometry and goes to the server when the
// mouse button is released.
export function TerrainView({ terrain }: { terrain: Terrain }) {
  const geometry = useMemo(() => terrainGeometry(terrain), [terrain]);
  useEffect(() => () => geometry.dispose(), [geometry]);
  const controls = useThree((s) => s.controls);
  const stroke = useRef<Stroke | null>(null);
  const ring = useRef<Mesh>(null);
  const tool = useEditor((s) => s.tool);
  const radius = useEditor((s) => s.brushRadius);

  function dab(x: number, z: number) {
    const current = stroke.current;
    const { tool, brushRadius, brushStrength } = useEditor.getState();
    if (!current || tool.kind !== 'brush') return;
    const brush: BrushDab = { kind: tool.brush, x, z, radius: brushRadius, strength: brushStrength, flattenTo: current.flattenTo };
    applyBrush(terrain, current.heights, brush);
    setHeights(geometry, current.heights);
  }

  function endStroke() {
    window.removeEventListener('pointerup', endStroke);
    if (controls && 'enabled' in controls) controls.enabled = true;
    const finished = stroke.current;
    stroke.current = null;
    if (finished) updateTerrain({ ...terrain, heights: finished.heights });
  }

  function onPointerDown(e: ThreeEvent<PointerEvent>) {
    if (tool.kind !== 'brush' || e.button !== 0) return;
    e.stopPropagation();
    // The orbit controls saw the press first; they stay put until the stroke ends.
    if (controls && 'enabled' in controls) controls.enabled = false;
    stroke.current = { heights: terrain.heights.slice(), flattenTo: heightAt(terrain, e.point.x, e.point.z) };
    window.addEventListener('pointerup', endStroke);
    dab(e.point.x, e.point.z);
  }

  function onPointerMove(e: ThreeEvent<PointerEvent>) {
    ring.current?.position.set(e.point.x, e.point.y + 0.3, e.point.z);
    if (stroke.current) dab(e.point.x, e.point.z);
  }

  function onClick(e: ThreeEvent<MouseEvent>) {
    // A drag that turned the view is not a click.
    if (e.delta > 4) return;
    if (tool.kind === 'place') void place(tool.what, e.point.x, e.point.z);
    else if (tool.kind === 'select') select(null);
  }

  return (
    <>
      <mesh name="terrain" geometry={geometry} onPointerDown={onPointerDown} onPointerMove={onPointerMove} onClick={onClick}>
        <meshLambertMaterial vertexColors />
      </mesh>
      {tool.kind === 'brush' && (
        <mesh ref={ring} rotation-x={-Math.PI / 2} raycast={() => null}>
          <ringGeometry args={[Math.max(0, radius - 0.5), radius, 64]} />
          <meshBasicMaterial color="#ffee88" transparent opacity={0.8} depthTest={false} />
        </mesh>
      )}
    </>
  );
}

const grass = new Color(92 / 255, 128 / 255, 60 / 255).convertSRGBToLinear();
const rock = new Color(118 / 255, 112 / 255, 104 / 255).convertSRGBToLinear();

// The same grid the server renders (SceneBuilder.TerrainChunk), coloured like it: grass, rock where steep.
function terrainGeometry(terrain: Terrain): BufferGeometry {
  const { resolution, size } = terrain;
  const row = resolution + 1;
  const cell = size / resolution;
  const positions = new Float32Array(row * row * 3);
  for (let iz = 0; iz <= resolution; iz++)
    for (let ix = 0; ix <= resolution; ix++) {
      const i = (iz * row + ix) * 3;
      positions[i] = ix * cell - size / 2;
      positions[i + 2] = iz * cell - size / 2;
    }
  const indices = new Uint32Array(resolution * resolution * 6);
  let n = 0;
  for (let iz = 0; iz < resolution; iz++)
    for (let ix = 0; ix < resolution; ix++) {
      const a = iz * row + ix;
      const b = a + 1;
      const c = a + row;
      const d = c + 1;
      indices.set([a, c, b, b, c, d], n);
      n += 6;
    }
  const geometry = new BufferGeometry();
  geometry.setAttribute('position', new BufferAttribute(positions, 3));
  geometry.setAttribute('color', new BufferAttribute(new Float32Array(row * row * 3), 3));
  geometry.setIndex(new BufferAttribute(indices, 1));
  setHeights(geometry, terrain.heights);
  return geometry;
}

function setHeights(geometry: BufferGeometry, heights: Float32Array) {
  const position = geometry.getAttribute('position');
  for (let i = 0; i < heights.length; i++) position.setY(i, heights[i]);
  position.needsUpdate = true;
  geometry.computeVertexNormals();
  geometry.computeBoundingSphere();
  const normal = geometry.getAttribute('normal');
  const color = geometry.getAttribute('color');
  const shade = new Color();
  for (let i = 0; i < heights.length; i++) {
    const rockiness = Math.min(1, Math.max(0, (0.92 - normal.getY(i)) * 6));
    shade.copy(grass).lerp(rock, rockiness);
    color.setXYZ(i, shade.r, shade.g, shade.b);
  }
  color.needsUpdate = true;
}
