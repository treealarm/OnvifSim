import { Line } from '@react-three/drei';
import { heightAt, type Terrain } from '../model';
import { useEditor } from '../store';

// The outline of what the real world offers under a click, drawn over the ground in a colour nothing
// else uses: the object can be recognised before it is built.
export function OsmView({ terrain }: { terrain: Terrain }) {
  const osm = useEditor((s) => s.osm);
  const candidate = osm?.candidates[osm.picked];
  if (!candidate || candidate.points.length < 2) return null;
  // A footprint is closed, a road or a tree is not.
  const outline =
    candidate.kind === 'highway' || candidate.kind === 'tree' ? candidate.points : [...candidate.points, candidate.points[0]];
  return (
    <Line
      points={outline.map((p) => [p.x, heightAt(terrain, p.x, p.z) + 1, p.z] as const)}
      color="#ffd400"
      lineWidth={2.5}
    />
  );
}
