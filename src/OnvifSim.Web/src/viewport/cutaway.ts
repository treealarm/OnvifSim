import { useThree } from '@react-three/fiber';
import { useEffect, useMemo } from 'react';
import { Plane, Vector3 } from 'three';
import { useEditor } from '../store';

// The editor's cutaway: a horizontal plane that hides everything above it, so a room built from
// wall plates can be seen, selected and edited from above. Local clipping only — the server's
// pictures are drawn from inside the room and need no help.
export const CUTAWAY_HEIGHT_M = 2.5;

// The plane keeps what is below it: the normal points down, and three.js clips away the side it
// points at. One instance, shared by every material that takes it.
const plane = new Plane(new Vector3(0, -1, 0), CUTAWAY_HEIGHT_M);

// The planes to give a mesh's material, or none while the cutaway is off. Clipping planes are in
// world space, so an object's own transform does not move the cut.
export function useCutawayPlanes(): Plane[] {
  const enabled = useEditor((s) => s.cutaway);
  const gl = useThree((s) => s.gl);
  useEffect(() => {
    gl.localClippingEnabled = true;
  }, [gl]);
  return useMemo(() => (enabled ? [plane] : []), [enabled]);
}
