using OnvifSim.Geometry;

namespace OnvifSim.World;

// The demo world: Biomolecular Sciences on the University of Maryland campus, where ta_vms's demo
// world places its cameras. Everything of the place is real and read out of OpenStreetMap — the
// building's footprint and height (way 24303349), the ring of roads round the block (Regents, Paint
// Branch and Technology Drive), the parking lots, the pond and the neighbours — and the numbers here
// are the demo's own constants, so the map and the simulator cannot disagree about where the building
// is. The doorway is this world's own: OSM does not mark entrances, and the demo needs one on the side
// the person walks out by. Each footprint carries its OSM way in a comment, so it can be checked
// against the source again.
//
// The building is one prism of its real outline: walls, roof and floor drawn from both sides, so a
// camera inside sees the inside and a camera outside sees the building. The hall pair works under that
// roof; the second pair works in Lot T, the parking lot the person crosses. What the editor cannot see
// that way is what its cutaway (a plane at 2.5 m) is for.
public static class MarylandWorld
{
    public const string Name = "maryland";

    // The world's anchor: the demo's, and North = 0, so the world's -Z is true north. DemoWorlds.cs
    // is told the same numbers.
    public const double Latitude = 38.9923;
    public const double Longitude = -76.9378;

    // ── The building ──────────────────────────────────────────────────────────

    // Biomolecular Sciences, OSM way 24303349: 20 corners, 54.8 × 31.3 m, and the height OSM gives.
    public static readonly (float X, float Z)[] BuildingOutline =
    [
        (3.3f, -81.4f), (3.3f, -76.9f), (10.9f, -77.0f), (12.0f, -77.0f), (12.1f, -72.9f),
        (32.6f, -72.9f), (32.5f, -69.0f), (35.3f, -69.0f), (35.3f, -63.8f), (35.1f, -63.0f),
        (32.4f, -62.8f), (32.4f, -50.3f), (-16.8f, -50.0f), (-17.0f, -62.1f), (-17.0f, -63.2f),
        (-19.4f, -63.1f), (-19.5f, -68.8f), (-17.0f, -68.9f), (-17.1f, -81.3f),
    ];

    public const float BuildingHeight = 11.78f;

    // The demo's Room 101, in WGS84: 14 × 10 m of the first floor the hall pair stands in. The building
    // has no wall across it — it is one hall under the roof — but the demo map places both cameras in
    // this room, so both lie inside it, and so does the indoor part of the walker's route.
    public const double Room101South = 38.9928704;
    public const double Room101West = -76.9379791;
    public const double Room101North = 38.9929603;
    public const double Room101East = -76.9378173;

    public const float Room101WestX = -15.5f, Room101EastX = -1.5f;
    public const float Room101NorthZ = -73.5f, Room101SouthZ = -63.5f;

    // The doorway in the north wall, on the walker's way out into the parking lot and back. The wall
    // splits around it and a lintel spans the top, so the opening is genuinely empty geometry.
    public const float DoorX = -8f;
    public const float DoorWidth = 1.8f;
    public const float DoorHeight = 2.2f;
    // Where the north wall stands along the walker's line, x = DoorX.
    public const float NorthWallZ = -81.35f;

    private const string WallColor = "#c8ccd2";
    private const string RoofColor = "#6f747c";
    // The floor's tiles and the seams between them: a hall floor reads as a floor, not as a sheet.
    private const string FloorColor = "#8f959d";
    private const string FloorSeamColor = "#6f747b";

    // A camera the demo places: ta_vms's camera id is the world's id behind the bench's prefix
    // (sim-master is the world's master). Height above the ground, heading clockwise from north, and
    // the horizontal field of view: for a PTZ camera, the wide end of its lens.
    public sealed record DemoCamera(
        string Id, string Name, CameraKind Kind,
        double Latitude, double Longitude, float Height, float Heading, float HorizontalFov, float Pitch);

    public static readonly DemoCamera[] Cameras =
    [
        // The pair of the demo's room: the master stands south of Room 101 on the line the person
        // walks out by, and looks along it — the room at 8–14 m, the doorway 24 m away — so one lens
        // holds the whole of the person's walk indoors. The slave stands in the room's south-east
        // corner, its back to the wall, and looks across the room: at 4.5–11 m from the person it
        // follows they cross its view slowly enough for a command every two seconds to hold them.
        new("master", "Main room master", CameraKind.Fixed,
            38.9928120, -76.9378925, 2.8f, 0f, 45, -8f),
        new("ptz", "Main room slave (PTZ)", CameraKind.Ptz,
            38.9928722, -76.9378196, 2.8f, 310f, 65, 0),
        // The second pair works in the parking lot: the master stands on a pole north-west of the
        // doorway, looking back along the line the person crosses, and its boxes are what aim the
        // slave. The person is 7.4–11.5 % of the master's picture height over the whole walk (the
        // detector needs 5 %); the slave sees them at 12–32 m.
        new("master2", "Building master 2", CameraKind.Fixed,
            38.9933061, -76.9381467, 6f, 125f, 55, -9.5f),
        new("ptz2", "Campus slave 2 (PTZ)", CameraKind.Ptz,
            38.9931085, -76.9380312, 5.5f, 88f, 60, 0),
    ];

    // The demo camera that is not placed on a wall: it rides the car.
    public const string VehicleCameraId = "vehicle";
    public const float VehicleCameraHeight = 1.55f;

    // ── The block and its neighbours ──────────────────────────────────────────

    // The ring the car drives: the centre line of the block's roads, closed, ~780 m round. Regents
    // Drive north and down the west bend, Technology Drive and the unnamed continuation south, Paint
    // Branch Drive east.
    private static readonly (float X, float Z)[] Ring =
    [
        (43, -151), (35, -151), (0, -150), (-97, -147), (-156, -145), (-191, -145), (-190, -139),
        (-182, -112), (-178, -96), (-174, -82), (-174, -71), (-176, -62), (-185, -41),
        (-173, -40), (-152, -40), (-133, -40), (-114, -40), (-105, -43), (-99, -45), (-44, -45),
        (-31, -37), (-19, -37), (-2, -37), (35, -37), (43, -37),
        (43, -57), (43, -87), (43, -149),
    ];

    // The three roads themselves; the car's route is the ring above.
    private static readonly (float X, float Z)[] Regents =
    [
        (43, -151), (35, -151), (0, -150), (-97, -147), (-156, -145), (-191, -145), (-190, -139),
        (-182, -112), (-178, -96), (-174, -82), (-174, -71), (-176, -62), (-185, -41),
    ];

    private static readonly (float X, float Z)[] Technology =
    [
        (43, -37), (35, -37), (-2, -37), (-19, -37), (-31, -37), (-44, -45), (-99, -45), (-105, -43),
        (-114, -40), (-133, -40), (-152, -40), (-173, -40), (-185, -41),
    ];

    // Paint Branch Drive runs on past the block; the part that is drawn stops at the terrain's edge.
    private static readonly (float X, float Z)[] PaintBranch =
    [
        (43, -151), (43, -149), (43, -87), (43, -57), (43, -37), (43, -31), (44, -19), (45, 96), (46, 200),
    ];

    // The parking lots and the pond the person walks past: way 25175122, 25174825, 519995841, 208126017.
    private static readonly (float X, float Z)[] LotT =
    [
        (4.4f, -108.4f), (4.4f, -107.6f), (-11.2f, -107.7f), (-16.7f, -107.7f), (-16.9f, -83.3f),
        (10.0f, -83.1f), (9.9f, -81.0f), (36.4f, -80.9f), (36.5f, -91.3f), (32.2f, -91.4f),
        (32.3f, -95.7f), (17.4f, -95.9f), (17.4f, -90.6f), (6.2f, -90.6f), (6.3f, -93.6f),
        (12.1f, -93.6f), (12.2f, -108.3f),
    ];

    private static readonly (float X, float Z)[] LotFf2 =
    [
        (-154.5f, -100.2f), (-154.3f, -86.2f), (-154.3f, -83.7f), (-116.6f, -84.3f), (-90.9f, -84.6f),
        (-91.1f, -101.1f),
    ];

    private static readonly (float X, float Z)[] LotYy =
    [
        (-11.3f, -113.4f), (-11.2f, -107.7f), (4.4f, -107.6f), (4.3f, -113.7f),
    ];

    private static readonly (float X, float Z)[] Pond =
    [
        (-76.5f, -56.0f), (-61.6f, -54.7f), (-37.4f, -54.4f), (-34.5f, -56.8f), (-34.2f, -74.1f),
        (-33.9f, -86.8f), (-37.6f, -89.1f), (-74.6f, -89.3f), (-78.2f, -88.4f), (-78.9f, -86.6f),
        (-79.1f, -60.4f),
    ];

    // The neighbours, with the heights OSM gives them: Manufacturing Building (24303373), Neutral
    // Buoyancy Research (24303369), Central Animal Resources Facility (24303442), Jeong H Kim
    // Engineering Building (23937556), Energy Research Facility (24303490), A. James Clark Hall
    // (363185813, whose outline is a courtyard ring and so is left to the map picture).
    private static readonly (string Id, float Height, (float X, float Z)[] Outline)[] Neighbours =
    [
        ("manufacturing", 12.52f,
        [
            (-121.8f, -47.3f), (-159.5f, -46.9f), (-159.7f, -63.0f), (-159.8f, -79.9f), (-122.1f, -80.3f),
            (-122.2f, -71.8f),
        ]),
        ("neutral-buoyancy", 15.31f,
        [
            (-112.4f, -51.3f), (-112.6f, -57.2f), (-112.5f, -75.1f), (-112.5f, -80.6f), (-90.7f, -80.7f),
            (-90.5f, -51.7f), (-102.1f, -51.3f), (-107.6f, -51.6f),
        ]),
        ("central-animal", 12f,
        [
            (-51.9f, 45.3f), (-53.0f, 47.6f), (-66.2f, 75.1f), (-67.6f, 78.0f), (-61.6f, 80.9f),
            (-51.7f, 85.6f), (-48.4f, 78.7f), (-42.9f, 81.3f), (-34.8f, 64.3f), (-40.2f, 61.7f),
            (-36.0f, 53.0f), (-51.5f, 45.6f),
        ]),
        ("jeong-h-kim", 20.71f,
        [
            (21.8f, 112.9f), (21.8f, 112.3f), (26.5f, 112.3f), (26.5f, 128.4f), (26.1f, 128.5f),
            (26.4f, 141.5f), (26.5f, 146.4f), (21.9f, 146.5f), (21.9f, 146.0f), (20.6f, 146.0f),
            (20.1f, 156.4f), (0.7f, 156.5f), (0.8f, 153.1f), (-2.2f, 152.8f), (-2.9f, 155.4f),
            (-0.8f, 156.3f), (-1.2f, 157.2f), (-3.6f, 160.6f), (-7.0f, 162.9f), (-8.4f, 163.6f),
            (-10.0f, 163.8f), (-13.5f, 163.6f), (-17.5f, 162.1f), (-20.2f, 159.9f), (-20.0f, 195.1f),
            (-23.3f, 195.2f), (-23.3f, 196.2f), (-23.3f, 197.5f), (-44.0f, 197.6f), (-44.0f, 197.3f),
            (-46.9f, 197.3f), (-46.9f, 193.9f), (-44.2f, 193.9f), (-44.2f, 192.1f), (-47.3f, 192.1f),
            (-47.4f, 184.4f), (-62.2f, 184.5f), (-62.4f, 171.6f), (-62.5f, 146.2f), (-64.0f, 146.2f),
            (-64.0f, 146.7f), (-68.5f, 146.8f), (-68.4f, 144.5f), (-68.8f, 112.7f), (-64.2f, 112.6f),
            (-64.2f, 113.2f), (-62.7f, 113.1f), (-62.7f, 112.6f), (-29.9f, 112.4f), (-29.9f, 113.1f),
            (-28.5f, 113.0f), (-28.5f, 112.5f), (-26.1f, 112.5f), (-21.7f, 112.5f), (-17.1f, 112.5f),
            (-13.9f, 112.4f), (-14.0f, 113.0f), (-12.4f, 113.0f), (-12.4f, 112.4f), (20.4f, 112.2f),
            (20.4f, 112.9f),
        ]),
        ("energy-research", 12f,
        [
            (56.0f, 3.4f), (56.1f, 27.2f), (56.3f, 53.3f), (56.4f, 69.0f), (56.4f, 80.0f), (106.5f, 79.7f),
            (106.5f, 66.9f), (119.2f, 66.8f), (119.1f, 50.5f), (106.1f, 50.5f), (105.8f, 3.2f),
            (97.3f, 3.2f), (97.2f, -21.6f), (83.1f, -21.7f), (83.1f, 3.3f),
        ]),
    ];

    // Trees stand where nothing else does: the park along Regents, the wood east of Paint Branch and
    // the grass south of the block. None on a road, a lot or under a building.
    private static readonly (float X, float Z, string Type, float Size)[] Trees =
    [
        (-170, -170, "tree.pine", 13), (-100, -160, "tree.oak", 9), (0, -158, "tree.pine", 12),
        (60, -140, "tree.oak", 10), (100, -120, "tree.pine", 12), (-60, -130, "tree.pine", 11),
        (150, 60, "tree.oak", 9), (-190, 20, "tree.pine", 11), (60, 110, "tree.oak", 10),
        (-45, 105, "tree.pine", 12),
    ];

    public static WorldModel Create()
    {
        var world = new WorldModel
        {
            Geo = new GeoModel { Latitude = Latitude, Longitude = Longitude, Elevation = 0, North = 0 },
        };

        // The ground's own picture: fetched once per installation into the data directory, never part
        // of the world file. The bounds are the extent these numbers cover, so a moved anchor shows up
        // as a map that no longer fits instead of one in the wrong place.
        var bounds = TerrainMaps.BoundsOf(world.Geo, world.Terrain.Size);
        world.Terrain.Map = "maryland-ground";
        world.Terrain.MapAttribution = "© OpenStreetMap contributors";
        world.Terrain.MapBounds = new MapBoundsModel { South = bounds.South, West = bounds.West, North = bounds.North, East = bounds.East };

        // The building: one prism of its real footprint, and a floor inside it. The floor stands a
        // little inside the walls and a little above the ground, so no tile shows outside and the two
        // do not z-fight.
        var outline = Normalized(BuildingOutline);
        var openings = new List<OpeningModel> { Doorway(outline) };
        AddPrism(world, "building", outline, BuildingHeight, WallColor, RoofColor, openings, seed: 1);
        AddPrism(world, "building-floor", NormalizePoints(Inset(outline, 0.96f)), 0.10f, FloorColor, FloorColor, null, seed: 2, texture: "tiles");

        // The racks the demo's plan draws, as plain boxes: the simulator does not repeat the demo's
        // tree of switches and ports, it only stands something in the room to recognize the corner by.
        for (int i = 0; i < 4; i++)
            Plate(world, $"rack-{i + 1}", -14.6f, 0, -72.4f + i * 2.5f, 0.6f, 2f, 1.2f, "#3d4149");

        Roads(world);

        // The person both pairs work on: a lap of Room 101, out through the doorway, north across the
        // parking lot, a turn to the right along it and back the same way. Both crossings run along the
        // doorway's line, so the person never walks through a wall.
        world.Walkers.Add(new WalkerModel
        {
            Id = "w1", Speed = 1.0f, Return = RouteReturn.Loop, Height = 1.75f,
            Route =
            [
                new(-8.5f, -72.5f), new(-11f, -72.5f), new(-11f, -67.5f), new(-6f, -67.5f), new(-6f, -72.5f),
                new(-8.5f, -72.5f),
                new(-8f, -78f), new(-8f, -86f), new(-8f, -91f), new(-8f, -96f), new(-8f, -100f),
                new(-6f, -100f), new(-2f, -100f), new(2f, -100f), new(6f, -100f), new(10f, -100f),
                new(6f, -100f), new(2f, -100f), new(-2f, -100f), new(-6f, -100f), new(-8f, -100f),
                new(-8f, -96f), new(-8f, -91f), new(-8f, -86f), new(-8f, -78f),
                new(-8.5f, -72.5f),
            ],
        });

        Ground(world);
        AddTrees(world);

        AddCameras(world);
        AddDevices(world);
        return world;
    }

    private static void Roads(WorldModel world)
    {
        world.Roads.Add(new RoadModel { Id = "regents-drive", Points = Line(Regents), Width = 7, Color = "#7d7f82" });
        world.Roads.Add(new RoadModel { Id = "paint-branch-drive", Points = Line(PaintBranch), Width = 7, Color = "#7d7f82" });
        world.Roads.Add(new RoadModel { Id = "technology-drive", Points = Line(Technology), Width = 7, Color = "#7d7f82" });
        // The car comes round the block again and again.
        world.Vehicles.Add(new VehicleModel
        {
            Id = "v1", Route = Line(Ring), Speed = 8, Return = RouteReturn.Loop,
            Color = "#2f6fde", Color2 = "#f0f0ea", Plate = "M777MM",
        });
    }

    // The lots and the pond the person walks past: flat pads, so the ground under them is asphalt or
    // water rather than grass.
    private static void Ground(WorldModel world)
    {
        AddPrism(world, "lot-t", Normalized(LotT), 0.04f, "#4f5358", "#4f5358", null, seed: 3, texture: "parking");
        AddPrism(world, "lot-ff2", Normalized(LotFf2), 0.04f, "#4f5358", "#4f5358", null, seed: 4, texture: "parking");
        AddPrism(world, "lot-yy", Normalized(LotYy), 0.04f, "#4f5358", "#4f5358", null, seed: 5, texture: "parking");
        AddPrism(world, "pond", Normalized(Pond), 0.03f, "#3f6b86", "#3f6b86", null, seed: 6, texture: "none");
        for (int i = 0; i < Neighbours.Length; i++)
        {
            var (id, height, outline) = Neighbours[i];
            AddPrism(world, id, Normalized(outline), height, WallColor, RoofColor, null, seed: 10 + i);
        }
    }

    private static void AddTrees(WorldModel world)
    {
        for (int i = 0; i < Trees.Length; i++)
        {
            var (x, z, type, size) = Trees[i];
            float across = type == "tree.pine" ? size * 0.35f : size * 0.7f;
            Add(world, $"tree-{i + 1}", type, x, 0, z, across, size, across, seed: i + 1);
        }
    }

    private static void AddCameras(WorldModel world)
    {
        foreach (var demo in Cameras)
        {
            var position = WorldGeo.ToWorld(world, demo.Latitude, demo.Longitude, demo.Height);
            var camera = new CameraModel
            {
                Id = demo.Id, Name = demo.Name, Kind = demo.Kind,
                Position = new Vec3(position.X, demo.Height, position.Z),
                Heading = demo.Heading, Pitch = demo.Pitch, HFov = demo.HorizontalFov,
                Users = [new UserModel { Username = "admin", Password = "admin" }],
            };
            if (demo.Kind == CameraKind.Ptz)
            {
                camera.Ptz = new PtzModel
                {
                    FovWide = demo.HorizontalFov, Tilt = -10, Home = new PtzPosition(0, -10, 0),
                };
                // A slave reports where its head points; ta_vms reads that as its telemetry. Its own
                // picture boxes nobody: only the master's boxes drive the aim.
                camera.Metadata = new MetadataModel { Analytics = false, PtzStatus = true };
            }
            world.Cameras.Add(camera);
        }

        // A camera bolted to the car: Position is the mount's offset in the car's own frame, and the
        // picture moves with the car.
        world.Cameras.Add(new CameraModel
        {
            Id = VehicleCameraId, Name = "Vehicle camera", Kind = CameraKind.Fixed,
            MountedOnVehicleId = "v1", Position = new Vec3(0, VehicleCameraHeight, -0.6f),
            Heading = 0, Pitch = -5, HFov = 70,
            Users = [new UserModel { Username = "admin", Password = "admin" }],
        });
    }

    // The master's three relays show every kind of link a relay can have: one lights a lamp standing
    // by the racks, one drives the slave camera's sensor, and one drives a sensor of the master itself.
    private static void AddDevices(WorldModel world)
    {
        var master = world.Cameras.Single(c => c.Id == "master");
        master.RelayOutputs.Add(new RelayOutputModel { Token = "lamp", Name = "Hall lamp relay" });
        master.RelayOutputs.Add(new RelayOutputModel { Token = "peer", Name = "Slave sensor relay" });
        master.RelayOutputs.Add(new RelayOutputModel { Token = "loop", Name = "Own sensor relay" });
        master.DigitalInputs.Add(new DigitalInputModel { Token = "input", Name = "Master sensor" });
        var ptz = world.Cameras.Single(c => c.Id == "ptz");
        ptz.DigitalInputs.Add(new DigitalInputModel { Token = "input", Name = "Slave sensor" });
        world.Lamps.Add(new LampModel
        {
            Id = "alert", Name = "Hall alert lamp", Position = new Vec3(-13.4f, 0, -68f),
        });
        world.Wires.Add(new WireModel { RelayCameraId = master.Id, RelayToken = "lamp", LampId = "alert" });
        world.Wires.Add(new WireModel
        {
            RelayCameraId = master.Id, RelayToken = "peer",
            InputCameraId = ptz.Id, InputToken = "input",
        });
        world.Wires.Add(new WireModel
        {
            RelayCameraId = master.Id, RelayToken = "loop",
            InputCameraId = master.Id, InputToken = "input",
        });
    }

    // The doorway sits on the wall segment the walker crosses: the segment is found from the point, so
    // the opening keeps its place whichever way the outline is stored.
    private static OpeningModel Doorway(IReadOnlyList<GroundPoint> ring)
    {
        var at = new GroundPoint(DoorX, NorthWallZ);
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            float dx = b.X - a.X, dz = b.Z - a.Z;
            float lengthSquared = dx * dx + dz * dz;
            if (lengthSquared <= 0) continue;
            float t = ((at.X - a.X) * dx + (at.Z - a.Z) * dz) / lengthSquared;
            if (t < 0 || t > 1) continue;
            float x = a.X + dx * t, z = a.Z + dz * t;
            float miss = (x - at.X) * (x - at.X) + (z - at.Z) * (z - at.Z);
            if (miss > 0.25f) continue;
            float length = MathF.Sqrt(lengthSquared);
            return new OpeningModel
            {
                Edge = i, Offset = t * length - DoorWidth / 2, Width = DoorWidth, Height = DoorHeight,
            };
        }
        throw new InvalidOperationException("The doorway is not on the building's footprint.");
    }

    private static List<GroundPoint> Normalized(IReadOnlyList<(float X, float Z)> points) =>
        Polygon.Normalize(points.Select(p => new GroundPoint(p.X, p.Z)).ToList());

    private static List<GroundPoint> NormalizePoints(IReadOnlyList<GroundPoint> ring) => Polygon.Normalize(ring);

    private static List<GroundPoint> Line(IReadOnlyList<(float X, float Z)> points) =>
        points.Select(p => new GroundPoint(p.X, p.Z)).ToList();

    // The outline shrunk towards its middle: a floor that cannot show outside its own walls.
    private static List<GroundPoint> Inset(IReadOnlyList<GroundPoint> ring, float factor)
    {
        float cx = ring.Average(p => p.X), cz = ring.Average(p => p.Z);
        return ring.Select(p => new GroundPoint(cx + (p.X - cx) * factor, cz + (p.Z - cz) * factor)).ToList();
    }

    private static void AddPrism(WorldModel world, string id, IReadOnlyList<GroundPoint> ring, float height,
        string wall, string roof, IReadOnlyList<OpeningModel>? openings, int seed, string? texture = null)
    {
        float cx = ring.Average(p => p.X), cz = ring.Average(p => p.Z);
        world.Objects.Add(new WorldObject
        {
            Id = id, Type = "prism",
            Position = new(cx, 0, cz), Scale = new(1, height, 1),
            Points = ring.Select(p => new GroundPoint(p.X - cx, p.Z - cz)).ToList(),
            Openings = openings?.ToList(), Color = wall, Color2 = roof, Seed = seed, Texture = texture,
        });
    }

    // A fixed seed per object: a tree's crown is drawn from it, so it must not change between two
    // builds of the world file.
    private static void Add(WorldModel world, string id, string type, float x, float y, float z,
        float sx, float sy, float sz, string? color = null, string? color2 = null, int seed = 0) =>
        world.Objects.Add(new WorldObject
        {
            Id = id, Type = type, Position = new(x, y, z), Scale = new(sx, sy, sz),
            Color = color, Color2 = color2, Seed = seed,
        });

    private static void Plate(WorldModel world, string id, float x, float y, float z,
        float sx, float sy, float sz, string color) =>
        Add(world, id, "box", x, y, z, sx, sy, sz, color: color);
}
