import type { ReactNode } from 'react';
import { focusOn, useEditor } from '../store';
import { focusPoint } from '../viewport/focusPoint';

// An inspector's heading with its Find button beside it.
export function InspectorTitle({ children }: { children: ReactNode }) {
  return (
    <div className="inspector-title">
      {children}
      <FindButton />
    </div>
  );
}

// Flies the viewport to what is being edited: a lamp is a metre of post and a person is a few pixels
// in a world hundreds of metres across, so clicking them in the scene is no way to find them.
export function FindButton() {
  const world = useEditor((s) => s.world);
  const selection = useEditor((s) => s.selection);
  const target = world && selection ? focusPoint(world, selection) : null;
  if (!target) return null;
  return (
    <button type="button" title="Fly the view to this" onClick={() => focusOn(target.point, target.distance)}>
      Find
    </button>
  );
}
