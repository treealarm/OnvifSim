import { Line } from '@react-three/drei';
import { heightAt, type GroundPoint, type Terrain } from '../model';
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
  const ring = (points: GroundPoint[]) =>
    points.map((p) => [p.x, heightAt(terrain, p.x, p.z) + 1, p.z] as const);
  return (
    <>
      <Line points={ring(outline)} color="#ffd400" lineWidth={2.5} />
      {/* A courtyard is outlined too: the building will be built around it, not over it. */}
      {candidate.kind !== 'highway' && candidate.kind !== 'tree'
        && candidate.holes.map((hole, index) => (
          <Line key={index} points={ring([...hole, hole[0]])} color="#ffd400" lineWidth={2.5} />
        ))}
    </>
  );
}
