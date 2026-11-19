using OnvifSim.World;

namespace OnvifSim.Geometry;

// Outlines for polygon geometry: what an outline has to be before it can be built (a simple ring with
// no repeated or collinear vertices, in one known direction), what it measures, how it is split into
// triangles, and whether a point lies inside it. One place, so the editor, the importer and the mesh
// builder all agree on what a valid footprint is.
//
// Sixteenth-of-a-metre detail is far below anything the world draws, so vertices that close are one
// point, and a corner that barely turns is on a straight line.
public static class Polygon
{
    public const float MinPointGapM = 0.01f;
    public const float MinCornerTurn = 1e-3f;
    public const float MinAreaM2 = 1f;

    // Two times the signed area of the triangle a, b, c in the ground plane. Negative is the direction
    // the outline is stored in: that is the order whose top face points up (+Y).
    public static float Cross(GroundPoint a, GroundPoint b, GroundPoint c) =>
        (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);

    // The shoelace area: negative for the stored direction, and its magnitude is what a footprint covers.
    public static float SignedArea(IReadOnlyList<GroundPoint> points)
    {
        float sum = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            sum += a.X * b.Z - b.X * a.Z;
        }
        return sum / 2;
    }

    // The outline as it should be stored: consecutive duplicates dropped, vertices that only stand on a
    // straight segment dropped too, and the direction normalised. A footprint too small to be one
    // comes back with fewer than three points, which the caller refuses with its own message.
    public static List<GroundPoint> Normalize(IReadOnlyList<GroundPoint> points)
    {
        var ring = new List<GroundPoint>(points.Count);
        foreach (var p in points)
            if (ring.Count == 0 || Distance(ring[^1], p) > MinPointGapM)
                ring.Add(p);
        // A ring given back to its first point is the same ring without it.
        while (ring.Count > 1 && Distance(ring[0], ring[^1]) <= MinPointGapM)
            ring.RemoveAt(ring.Count - 1);

        bool removed;
        do
        {
            removed = false;
            for (int i = 0; i < ring.Count && ring.Count > 3; i++)
            {
                var previous = ring[(i + ring.Count - 1) % ring.Count];
                var next = ring[(i + 1) % ring.Count];
                if (MathF.Abs(Cross(previous, ring[i], next)) > MinCornerTurn) continue;
                // The corner stands on the segment between its neighbours: dropping it loses nothing.
                ring.RemoveAt(i);
                removed = true;
                break;
            }
        } while (removed);

        if (ring.Count >= 3 && SignedArea(ring) > 0)
            ring.Reverse();
        return ring;
    }

    // A hole as it should be stored: the same normalization, then the opposite direction, so its walls
    // face into the courtyard and the bridges of the cap triangulation run the right way.
    public static List<GroundPoint> NormalizeHole(IReadOnlyList<GroundPoint> points)
    {
        var ring = Normalize(points);
        ring.Reverse();
        return ring;
    }

    // Whether no two of its segments cross. An outline that crosses itself has no inside and cannot be
    // triangulated, so it is refused rather than repaired.
    public static bool IsSimple(IReadOnlyList<GroundPoint> points)
    {
        int n = points.Count;
        if (n < 3) return false;
        for (int i = 0; i < n; i++)
        {
            var a0 = points[i];
            var a1 = points[(i + 1) % n];
            for (int j = i + 1; j < n; j++)
            {
                // Neighbouring segments share a vertex; that is not a crossing.
                if (j == i || j == (i + 1) % n || (j + 1) % n == i) continue;
                var b0 = points[j];
                var b1 = points[(j + 1) % n];
                if (SegmentsCross(a0, a1, b0, b1)) return false;
            }
        }
        return true;
    }

    // Whether any two segments of the two rings touch or cross.
    public static bool RingsCross(IReadOnlyList<GroundPoint> a, IReadOnlyList<GroundPoint> b)
    {
        if (a.Count < 3 || b.Count < 3) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var a0 = a[i];
            var a1 = a[(i + 1) % a.Count];
            for (int j = 0; j < b.Count; j++)
                if (SegmentsCross(a0, a1, b[j], b[(j + 1) % b.Count])) return true;
        }
        return false;
    }

    // Whether a ring is a hole of an outline: every corner inside it and no edge touching it. A hole
    // lying on the wall leaves no wall at all, so it is refused too.
    public static bool HoleFits(IReadOnlyList<GroundPoint> outline, IReadOnlyList<GroundPoint> hole)
    {
        if (outline.Count < 3 || hole.Count < 3 || RingsCross(outline, hole)) return false;
        foreach (var p in hole)
            if (!Contains(outline, p)) return false;
        return true;
    }

    // The outline as triangles, three vertex indices each, wound like the outline (top faces up). The
    // outline must be normalised and simple; a call that cannot make progress returns what it had, and
    // validation refuses such a footprint before it ever gets here.
    public static List<int> Triangulate(IReadOnlyList<GroundPoint> points)
    {
        var triangles = new List<int>();
        if (points.Count < 3) return triangles;
        var ring = new List<int>(points.Count);
        for (int i = 0; i < points.Count; i++) ring.Add(i);
        Clip(points, ring, triangles);
        return triangles;
    }

    // The footprint as triangles with its courtyards left open, three corners to a triangle. Each hole
    // is joined to the ring by a zero-width bridge and the whole is clipped as one. A footprint whose
    // holes cannot be joined returns nothing rather than a lid over them: validation refuses such a
    // footprint before it is ever drawn. The outline and the holes must already be normalised, the
    // holes the other way round.
    public static List<GroundPoint> Triangulate(IReadOnlyList<GroundPoint> outline,
        IReadOnlyList<IReadOnlyList<GroundPoint>>? holes)
    {
        var flat = new List<GroundPoint>();
        if (outline.Count < 3) return flat;
        if (holes is null || holes.Count == 0)
        {
            foreach (int i in Triangulate(outline)) flat.Add(outline[i]);
            return flat;
        }

        // One point list for the outline and every hole; the ring is the order they are clipped in.
        var points = new List<GroundPoint>(outline);
        var ring = new List<int>(outline.Count);
        for (int i = 0; i < outline.Count; i++) ring.Add(i);

        for (int h = 0; h < holes.Count; h++)
        {
            var hole = holes[h];
            if (hole.Count < 3) return [];
            int start = points.Count;
            points.AddRange(hole);
            var holeRing = new List<int>(hole.Count);
            for (int i = 0; i < hole.Count; i++) holeRing.Add(start + i);

            // The bridge leaves the hole at its rightmost corner: the rest of that ring is behind it.
            int corner = 0;
            for (int i = 1; i < holeRing.Count; i++)
                if (points[holeRing[i]].X > points[holeRing[corner]].X) corner = i;
            int at = BridgeTarget(points, ring, holeRing, corner, holes, h);
            if (at < 0) return [];

            // The ring visits the target, crosses to the hole's corner, walks the hole once, returns
            // over the same bridge and carries on: both ends of the bridge appear twice.
            var merged = new List<int>(ring.Count + holeRing.Count + 2);
            for (int i = 0; i <= at; i++) merged.Add(ring[i]);
            merged.Add(holeRing[corner]);
            for (int k = 1; k < holeRing.Count; k++) merged.Add(holeRing[(corner + k) % holeRing.Count]);
            merged.Add(holeRing[corner]);
            merged.Add(ring[at]);
            for (int i = at + 1; i < ring.Count; i++) merged.Add(ring[i]);
            ring = merged;
        }

        var triangles = new List<int>();
        if (!Clip(points, ring, triangles)) return [];
        foreach (int i in triangles) flat.Add(points[i]);
        return flat;
    }

    // Where in the ring a hole's rightmost corner can be joined: the nearest corner of the ring that
    // the bridge reaches without crossing a wall or another hole and without running through a court.
    private static int BridgeTarget(List<GroundPoint> points, List<int> ring, List<int> holeRing, int corner,
        IReadOnlyList<IReadOnlyList<GroundPoint>> holes, int currentHole)
    {
        var from = points[holeRing[corner]];
        foreach (int at in Enumerable.Range(0, ring.Count).OrderBy(t => DistanceSquared(points[ring[t]], from)))
            if (BridgeIsClear(points, ring, from, points[ring[at]], ring[at], holes, currentHole)) return at;
        return -1;
    }

    // Whether a bridge runs through nothing: not across a wall of the ring it joins, not across a hole
    // still to be joined, and not through any courtyard.
    private static bool BridgeIsClear(IReadOnlyList<GroundPoint> points, List<int> ring, GroundPoint from,
        GroundPoint to, int target, IReadOnlyList<IReadOnlyList<GroundPoint>> holes, int currentHole)
    {
        var mid = new GroundPoint((from.X + to.X) / 2, (from.Z + to.Z) / 2);
        foreach (var hole in holes)
            if (Contains(hole, mid)) return false;

        for (int i = 0; i < ring.Count; i++)
        {
            int j = (i + 1) % ring.Count;
            if (ring[i] == target || ring[j] == target) continue;
            if (SegmentsCross(from, to, points[ring[i]], points[ring[j]])) return false;
        }
        // The holes joined so far are part of the ring already; the ones still waiting are not.
        for (int h = currentHole + 1; h < holes.Count; h++)
        {
            var hole = holes[h];
            for (int i = 0; i < hole.Count; i++)
                if (SegmentsCross(from, to, hole[i], hole[(i + 1) % hole.Count])) return false;
        }
        return true;
    }

    // Ear clipping of one ring, whose entries are indices into points. A bridge passes through its two
    // ends twice, so a vertex coinciding with the ear's own corner is not a different corner. False
    // when the ring could not be reduced to a triangle.
    private static bool Clip(IReadOnlyList<GroundPoint> points, List<int> ring, List<int> triangles)
    {
        if (ring.Count < 3) return false;
        bool clipped = true;
        while (ring.Count > 3 && clipped)
        {
            clipped = false;
            for (int i = 0; i < ring.Count; i++)
            {
                int i0 = ring[(i + ring.Count - 1) % ring.Count];
                int i1 = ring[i];
                int i2 = ring[(i + 1) % ring.Count];
                var a = points[i0];
                var b = points[i1];
                var c = points[i2];
                // Reflex or straight: a corner of the ring, not an ear.
                if (Cross(a, b, c) >= -MinCornerTurn) continue;
                bool catchesAnother = false;
                foreach (int j in ring)
                {
                    if (j == i0 || j == i1 || j == i2) continue;
                    // The other copy of a bridge's end is the same point, not an obstacle.
                    if (points[j] == a || points[j] == b || points[j] == c) continue;
                    if (InsideTriangle(points[j], a, b, c)) { catchesAnother = true; break; }
                }
                if (catchesAnother) continue;
                triangles.Add(i0);
                triangles.Add(i1);
                triangles.Add(i2);
                ring.RemoveAt(i);
                clipped = true;
                break;
            }
        }
        if (ring.Count != 3) return false;
        triangles.Add(ring[0]);
        triangles.Add(ring[1]);
        triangles.Add(ring[2]);
        return true;
    }

    // Whether a point lies inside an outline, by counting the crossings of a ray to the east. Points
    // exactly on an edge count as inside, which is what a click on a building's wall should do.
    public static bool Contains(IReadOnlyList<GroundPoint> points, GroundPoint p)
    {
        int n = points.Count;
        if (n < 3) return false;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var a = points[i];
            var b = points[j];
            if (OnSegment(a, b, p)) return true;
            if ((a.Z > p.Z) != (b.Z > p.Z) &&
                p.X < (b.X - a.X) * (p.Z - a.Z) / (b.Z - a.Z) + a.X)
                inside = !inside;
        }
        return inside;
    }

    // Whether a point lies inside the footprint and outside every courtyard.
    public static bool Contains(IReadOnlyList<GroundPoint> outline,
        IReadOnlyList<IReadOnlyList<GroundPoint>>? holes, GroundPoint p)
    {
        if (!Contains(outline, p)) return false;
        if (holes is null) return true;
        foreach (var hole in holes)
            if (Contains(hole, p)) return false;
        return true;
    }

    private static float Distance(GroundPoint a, GroundPoint b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static float DistanceSquared(GroundPoint a, GroundPoint b) =>
        (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);

    // Whether c lies inside the triangle a, b, c0, all wound the stored way (negative crosses).
    private static bool InsideTriangle(GroundPoint p, GroundPoint a, GroundPoint b, GroundPoint c) =>
        Cross(a, b, p) <= MinCornerTurn && Cross(b, c, p) <= MinCornerTurn && Cross(c, a, p) <= MinCornerTurn;

    private static bool OnSegment(GroundPoint a, GroundPoint b, GroundPoint p)
    {
        float cross = Cross(a, b, p);
        if (MathF.Abs(cross) > 1e-4f) return false;
        return p.X >= MathF.Min(a.X, b.X) - 1e-4f && p.X <= MathF.Max(a.X, b.X) + 1e-4f &&
               p.Z >= MathF.Min(a.Z, b.Z) - 1e-4f && p.Z <= MathF.Max(a.Z, b.Z) + 1e-4f;
    }

    private static bool SegmentsCross(GroundPoint a0, GroundPoint a1, GroundPoint b0, GroundPoint b1)
    {
        float d1 = Cross(b0, b1, a0), d2 = Cross(b0, b1, a1);
        float d3 = Cross(a0, a1, b0), d4 = Cross(a0, a1, b1);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            return true;
        return (MathF.Abs(d1) <= 1e-4f && OnSegment(b0, b1, a0)) ||
               (MathF.Abs(d2) <= 1e-4f && OnSegment(b0, b1, a1)) ||
               (MathF.Abs(d3) <= 1e-4f && OnSegment(a0, a1, b0)) ||
               (MathF.Abs(d4) <= 1e-4f && OnSegment(a0, a1, b1));
    }
}
