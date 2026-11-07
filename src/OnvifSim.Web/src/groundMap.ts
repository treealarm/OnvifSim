import { useEffect, useRef } from 'react';
import { sameBounds, terrainBounds } from './model';
import { bakeGroundMap, useEditor } from './store';

// How long the world has to hold still before its picture is read again: the north is a slider and
// the anchor a field, and every step between the two ends is not an extent of its own.
const settleMs = 1200;

// The picture under the ground follows the world it lies under. A world that names a map has its
// extent fetched once; move the anchor, the size or the north, and this reads it again for the new
// one — so the campus turns with the world instead of the map simply vanishing. One attempt per
// extent: a provider that refused stays refused until someone asks again from the panel.
export function useGroundMapFollow() {
  const world = useEditor((s) => s.world);
  const baking = useEditor((s) => s.mapBaking);
  const attempted = useRef<string | null>(null);

  useEffect(() => {
    if (!world || baking) return;
    const terrain = world.terrain;
    if (!terrain.map || terrain.showMap === false) return;
    const live = terrainBounds(world.geo, terrain.size);
    if (terrain.mapBounds && sameBounds(live, terrain.mapBounds)) return;

    const extent = `${world.geo.latitude},${world.geo.longitude},${world.geo.north},${terrain.size}`;
    if (attempted.current === extent) return;
    const timer = window.setTimeout(() => {
      attempted.current = extent;
      void bakeGroundMap();
    }, settleMs);
    return () => window.clearTimeout(timer);
  }, [world, baking]);
}
