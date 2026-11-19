import { Line } from '@react-three/drei';
import { useThree, type ThreeEvent } from '@react-three/fiber';
import { useEffect } from 'react';
import { footprintOf, ringOf, toWorldPoint, withPoint, type RingIndex } from '../footprint';
import { heightAt, type GroundPoint, type Terrain, type WorldObject } from '../model';
import { beginEdgeInsert, beginVertexDrag, cancelVertexDrag, finishVertexDrag, useEditor } from '../store';

// The vertices of the selected building and the outline as it is being edited. A sphere on a corner
// drags it over the ground; a cube on the middle of an edge adds a corner there and drags it at once.
// The object itself does not move until the pointer is released — the bright line is the shape being
// drawn, and the mesh catches up in one edit.
export function FootprintView({ obj, terrain }: { obj: WorldObject; terrain: Terrain }) {
  const controls = useThree((s) => s.controls);
  const drag = useEditor((s) => s.draggedVertex);
  const active = drag?.objectId === obj.id ? drag : null;
  const frame = active ? withPoint(active.working, active.ring, active.index, active.point) : footprintOf(obj);
  const rings: RingIndex[] = [-1, ...frame.holes.map((_, hole) => hole)];

  useEffect(() => {
    if (!active) return;
    const cancel = (e: KeyboardEvent) => {
      if (e.key === 'Escape') cancelVertexDrag();
    };
    window.addEventListener('keydown', cancel);
    return () => window.removeEventListener('keydown', cancel);
  }, [active]);

  function grab(e: ThreeEvent<PointerEvent>, start: () => void) {
    if (e.button !== 0 || useEditor.getState().tool.kind !== 'select') return;
    e.stopPropagation();
    // The orbit controls saw the press first; they stay put until the vertex is released.
    if (controls && 'enabled' in controls) controls.enabled = false;
    start();
    const release = () => {
      window.removeEventListener('pointerup', release);
      if (controls && 'enabled' in controls) controls.enabled = true;
      finishVertexDrag();
    };
    window.addEventListener('pointerup', release);
  }

  // A ground point on the object's own footprint, a little above the ground it lies on.
  function ground(p: GroundPoint, lift: number) {
    const world = toWorldPoint(obj, p);
    return [world.x, heightAt(terrain, world.x, world.z) + obj.position.y + lift, world.z] as const;
  }

  function midpoint(a: GroundPoint, b: GroundPoint) {
    const from = toWorldPoint(obj, a), to = toWorldPoint(obj, b);
    const x = (from.x + to.x) / 2, z = (from.z + to.z) / 2;
    return [x, heightAt(terrain, x, z) + obj.position.y + 0.5, z] as const;
  }

  return (
    <>
      {frame.points.length > 1 && (
        <Line points={[...frame.points, frame.points[0]].map((p) => ground(p, 0.4))} color="#ffffff" lineWidth={1.5} />
      )}
      {frame.holes.map((hole, index) => (
        hole.length > 1 && (
          <Line key={`hole-${index}`} points={[...hole, hole[0]].map((p) => ground(p, 0.4))} color="#9fd8ff" lineWidth={1.5} />
        )
      ))}
      {rings.map((ring) =>
        ringOf(frame, ring).map((p, index) => {
          const dragged = active?.ring === ring && active.index === index;
          return (
            <mesh
              key={`v-${ring}-${index}`}
              position={ground(p, 0.6)}
              onPointerDown={(e) => grab(e, () => beginVertexDrag(obj.id, ring, index))}
            >
              <sphereGeometry args={[dragged ? 1.1 : 0.8, 12, 8]} />
              <meshBasicMaterial color={dragged ? '#44dd66' : ring < 0 ? '#ffcc33' : '#66bbff'} />
            </mesh>
          );
        }),
      )}
      {rings.map((ring) => {
        const points = ringOf(frame, ring);
        return points.map((p, index) => (
          <mesh
            key={`e-${ring}-${index}`}
            position={midpoint(p, points[(index + 1) % points.length])}
            onPointerDown={(e) => grab(e, () => beginEdgeInsert(obj.id, ring, index))}
          >
            <boxGeometry args={[0.45, 0.45, 0.45]} />
            <meshBasicMaterial color="#cccccc" />
          </mesh>
        ));
      })}
    </>
  );
}
