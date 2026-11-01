using OnvifSim.World;

namespace OnvifSim.Analytics;

public readonly record struct TrackedObject(int ObjectId, string ObjectKey, DetectionKind Kind, FrameBox Box, GroundPose Pose);

// Gives each person and car a camera sees an object id that stays theirs for as long as they stay in
// view. Like a camera's own tracker it does not recognise objects: one that leaves the picture and
// comes back is a new object with a new id. Not thread-safe: one tracker belongs to one camera's
// analytics.
public sealed class ObjectTracker
{
    private Dictionary<string, int> _ids = [];
    private int _nextId = 1;

    public List<TrackedObject> Update(IReadOnlyList<Sighting> sightings)
    {
        var ids = new Dictionary<string, int>();
        var objects = new List<TrackedObject>(sightings.Count);
        foreach (var sighting in sightings)
        {
            string key = $"{(sighting.Kind == DetectionKind.Person ? "person" : "vehicle")}:{sighting.ObjectId}";
            if (!_ids.TryGetValue(key, out int id)) id = _nextId++;
            ids[key] = id;
            objects.Add(new TrackedObject(id, sighting.ObjectId, sighting.Kind, sighting.Box, sighting.Pose));
        }
        _ids = ids;
        return objects;
    }
}
