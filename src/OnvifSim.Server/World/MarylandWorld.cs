namespace OnvifSim.World;

// The demo world: the A.V. Williams building on the University of Maryland campus, where ta_vms's
// demo world (dotnet/VmsMap/Demo/DemoWorlds.cs) places its cameras. Its anchor, the building
// envelope and the camera poses are the demo's own constants, so the map and the simulator cannot
// disagree about where the building is; everything around them — the ring road, the car and its
// camera, the trees and the neighbouring office — is made up here, because the map does not know it
// and need not.
//
// The building is one big hall, not a row of rooms: the demo's hall master and PTZ slave work as a
// pair (the master's boxes drive the slave's aim), so they stand in one space with a person walking
// between them. The person's route also takes them out of the doorway in the north wall, toward the
// ring road, where the second pair is: its master watches the road and the slave turns after the
// person outside as well as after the car. Every wall is a box plate, never one solid box: the
// rasterizer culls back faces, so a solid building is invisible from the inside. Floor, outer walls
// and roof are slabs, and a camera in the hall sees its inside the way a person in it would. What
// the editor cannot see that way is what its cutaway (a plane at 2.5 m) is for.
public static class MarylandWorld
{
    public const string Name = "maryland";

    // The demo building in WGS84: the anchor is the centre of the envelope, and North = 0, so the
    // world's -Z is true north. DemoWorlds.cs's Building* constants.
    public const double Latitude = 38.9923;
    public const double Longitude = -76.9378;
    public const double BuildingSouth = 38.9918;
    public const double BuildingWest = -76.9390;
    public const double BuildingNorth = 38.9928;
    public const double BuildingEast = -76.9365;

    // The demo's Room 101 in WGS84: the corner of the hall the hall pair stands in. The hall has no
    // wall there — it is one big room — but the demo map places both cameras in this room, so both
    // lie inside it, and so does the indoor part of the walker's route.
    public const double Room101South = 38.99238;
    public const double Room101West = -76.93855;
    public const double Room101North = 38.99250;
    public const double Room101East = -76.93835;

    // The top of the walls and the underside of the roof. Above the editor's cutaway (2.5 m), so the
    // hall can be worked on from above, and above the cameras standing in it.
    public const float InteriorHeight = 3.6f;

    private const float WallThickness = 0.4f;

    // The doorway in the north wall, on the walker's way out of the building and back: the only
    // opening between the hall and the ground the road pair watches. The wall splits around it and
    // a lintel spans the top, so the opening is genuinely empty geometry — a wall panel there
    // would stop both the person's route and the cameras' lines of sight.
    public const float DoorX = -50f;
    public const float DoorWidth = 1.8f;
    public const float DoorHeight = 2.2f;

    private const string FloorColor = "#8f959d";
    private const string WallColor = "#c8ccd2";
    private const string RoofColor = "#6f747c";
    // Where the demo's Room 101 lies in the hall: its rectangle's north-east corner is where the
    // racks stand and the pair's corner of the floor is.
    private const float RackNorthZ = -21.06f;
    private const float RackWestX = -61.9f;

    // A camera the demo places: ta_vms's camera id is the world's id behind the bench's prefix
    // (sim-master is the world's master). Height above the building floor, heading clockwise from
    // north, and the horizontal field of view: for a PTZ camera, the wide end of its lens.
    public sealed record DemoCamera(
        string Id, string Name, CameraKind Kind,
        double Latitude, double Longitude, float Height, float Heading, float HorizontalFov, float Pitch);

    public static readonly DemoCamera[] Cameras =
    [
        // The pair of the demo: one room, the master in its south-west corner looking across it and
        // the slave in the north-east corner looking back. A person walks between them.
        new("master", "Main room master", CameraKind.Fixed,
            38.99240, -76.93852, 2.8f, 55, 70, -18),
        new("ptz", "Main room slave (PTZ)", CameraKind.Ptz,
            38.992484, -76.938372, 2.8f, 235, 65, 0),
        // The second pair works as one too, on the road north of the building: the master stands
        // on a pole outside the north wall looking across the ring road, and the slave north of
        // the road looking south. The slave sees both the lane and the doorway the person walks
        // out of, so it can turn after the car and after a person on the ground between. The car
        // is 7.4% of the master's picture height across the whole frame (the detector needs 5%),
        // so its own analytics drive the slave; the master is an overview: the whole stretch of
        // road is in its picture, and the person never comes closer than about 12 m to it.
        new("master2", "Building master 2", CameraKind.Fixed,
            38.992884, -76.938447, 6f, 0, 60, -12),
        new("ptz2", "Campus slave 2 (PTZ)", CameraKind.Ptz,
            38.993468, -76.938447, 6f, 180, 60, 0),
    ];

    // The demo camera that is not placed on a wall: it rides the car.
    public const string VehicleCameraId = "vehicle";
    public const float VehicleCameraHeight = 1.55f;

    public static WorldModel Create()
    {
        var world = new WorldModel
        {
            Geo = new GeoModel { Latitude = Latitude, Longitude = Longitude, Elevation = 0, North = 0 },
        };

        var (westX, northZ) = Ground(world, BuildingNorth, BuildingWest);
        var (eastX, southZ) = Ground(world, BuildingSouth, BuildingEast);

        // The hall: a floor slab, four walls and a roof slab.
        float cx = (eastX + westX) / 2, cz = (southZ + northZ) / 2;
        float width = eastX - westX, depth = southZ - northZ;
        // The floor's top sits just above the terrain: at exactly its height the two z-fight, and the
        // cameras see grass grow through the floor.
        Plate(world, "building-floor", cx, -0.25f, cz, width, 0.3f, depth, FloorColor);
        Plate(world, "building-roof", cx, InteriorHeight, cz, width, 0.35f, depth, RoofColor);
        NorthWall(world, westX - WallThickness / 2, northZ, eastX + WallThickness / 2);
        Plate(world, "building-wall-s", cx, 0, southZ, width + WallThickness, InteriorHeight, WallThickness, WallColor);
        Plate(world, "building-wall-w", westX, 0, cz, WallThickness, InteriorHeight, depth - WallThickness, WallColor);
        Plate(world, "building-wall-e", eastX, 0, cz, WallThickness, InteriorHeight, depth - WallThickness, WallColor);

        // The racks the demo's plan draws, as plain boxes: the simulator does not repeat the demo's
        // tree of switches and ports, it only stands something in the hall to recognize the corner
        // by. They stand where the demo's Room 101 is, along the row the master looks down.
        for (int i = 0; i < 4; i++)
            Plate(world, $"rack-{i + 1}", RackWestX + i * 3.2f, 0, RackNorthZ, 0.6f, 2f, 1.2f, "#3d4149");

        // The landscape: a ring road round the building, the car that drives it, a parking apron and
        // a neighbouring office. The route is closed: the car comes round again and again.
        GroundPoint[] ring =
        [
            new(-140, -100), new(60, -100), new(170, -40), new(170, 60),
            new(60, 110), new(-140, 110), new(-175, 40), new(-175, -40),
        ];
        world.Roads.Add(new RoadModel { Id = "ring-road", Points = [.. ring], Width = 7, Color = "#7d7f82", Closed = true });
        world.Vehicles.Add(new VehicleModel
        {
            Id = "v1", Route = [.. ring], Speed = 8, Return = RouteReturn.Loop,
            Color = "#2f6fde", Color2 = "#f0f0ea", Plate = "M777MM",
        });
        // The person both pairs work on: the hall master sees them inside Room 101 and boxes them,
        // and the hall slave aims where those boxes say. The route then leaves the room through the
        // hall and the doorway, walks north toward the ring road — where the road master picks them
        // up and its slave turns after them — and comes back through the same doorway. The closing
        // segment of the loop runs along the doorway's line, so the person never crosses a wall.
        world.Walkers.Add(new WalkerModel
        {
            Id = "w1", Speed = 1.0f, Return = RouteReturn.Loop, Height = 1.75f,
            Route =
            [
                new(-50, -13), new(-54, -13), new(-54, -18), new(-59, -18), new(-59, -13),
                new(-50, -13), new(-50, -56), new(-50, -72), new(-52, -82), new(-50, -90),
            ],
        });
        Plate(world, "parking", 80, 0, 142, 90, 0.06f, 24, "#5f6165");

        Add(world, "office-ne", "office", 165, 0, 145, 44, 11, 28, "#d0d4da", "#5c6068");
        Scenery(world);

        AddCameras(world);
        AddDevices(world);
        return world;
    }

    // The north wall in three plates: either side of the doorway, and the lintel over it. The
    // person walks out through the gap, and the cameras' lines of sight pass through it too.
    private static void NorthWall(WorldModel world, float westEdge, float northZ, float eastEdge)
    {
        float doorWest = DoorX - DoorWidth / 2, doorEast = DoorX + DoorWidth / 2;
        Plate(world, "building-wall-n-w", (westEdge + doorWest) / 2, 0, northZ,
            doorWest - westEdge, InteriorHeight, WallThickness, WallColor);
        Plate(world, "building-wall-n-e", (doorEast + eastEdge) / 2, 0, northZ,
            eastEdge - doorEast, InteriorHeight, WallThickness, WallColor);
        Plate(world, "building-wall-n-lintel", DoorX, DoorHeight, northZ, DoorWidth,
            InteriorHeight - DoorHeight, WallThickness, WallColor);
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
    // in the hall, one drives the slave camera's sensor, and one drives a sensor of the master itself.
    // Closed, the contact closes the sensors' circuits (their idle state is open, so they read
    // active) and the lamp is on; open, the sensors read idle and the lamp is off.
    // Relays and sensors are settings of a camera, not things standing in the world, so the car carries
    // none: a device on it would be a link nobody can see, and the static pair already shows each kind.
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
            Id = "alert", Name = "Hall alert lamp", Position = new Vec3(-56, 0, -16),
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

    private static void Scenery(WorldModel world)
    {
        (float X, float Z, string Type, float Size)[] trees =
        [
            (-192, -70, "tree.pine", 13), (-186, 120, "tree.pine", 11), (-110, -150, "tree.oak", 9),
            (0, -160, "tree.pine", 12), (120, -150, "tree.oak", 10), (195, -110, "tree.pine", 12),
            (198, 20, "tree.oak", 9), (-60, 158, "tree.pine", 11), (10, 160, "tree.oak", 10),
            (140, 168, "tree.pine", 12),
        ];
        for (int i = 0; i < trees.Length; i++)
        {
            var (x, z, type, size) = trees[i];
            Add(world, $"tree-{i + 1}", type, x, 0, z, type == "tree.pine" ? size * 0.35f : size * 0.7f, size,
                type == "tree.pine" ? size * 0.35f : size * 0.7f, seed: i + 1);
        }
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

    private static (float X, float Z) Ground(WorldModel world, double latitude, double longitude)
    {
        var point = WorldGeo.ToWorld(world, latitude, longitude, 0);
        return (point.X, point.Z);
    }
}
