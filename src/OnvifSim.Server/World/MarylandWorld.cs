namespace OnvifSim.World;

// The demo world: the A.V. Williams building on the University of Maryland campus, where ta_vms's
// demo world (dotnet/VmsMap/Demo/DemoWorlds.cs) places its cameras. Its anchor, the building
// envelope, the two rooms and the camera poses are the demo's own constants, so the map and the
// simulator cannot disagree about where the building is; everything around them — the ring road,
// the car and its camera, the trees and the neighbouring office — is made up here, because the map
// does not know it and need not.
//
// Every wall is a box plate, never one solid box: the rasterizer culls back faces, so a solid
// building is invisible from the inside. Floor, outer walls, roof and the two rooms are slabs, and
// a camera standing in a room sees the faces of that room the way a person in it would. What the
// editor cannot see that way is what its cutaway (a plane at 2.5 m) is for.
public static class MarylandWorld
{
    public const string Name = "maryland";

    // The demo building in WGS84: the anchor is the centre of the envelope, and North = 0, so the
    // world's -Z is true north. DemoWorlds.cs's Building* and Room* constants.
    public const double Latitude = 38.9923;
    public const double Longitude = -76.9378;
    public const double BuildingSouth = 38.9918;
    public const double BuildingWest = -76.9390;
    public const double BuildingNorth = 38.9928;
    public const double BuildingEast = -76.9365;
    public const double Room101South = 38.99238;
    public const double Room101West = -76.93855;
    public const double Room101North = 38.99250;
    public const double Room101East = -76.93835;
    // Room 102 is a diagram on the floor plan in the demo, not a rectangle: the simulator gives it
    // the size of Room 101 around the point DemoWorlds puts it at.
    public const double Room102Latitude = 38.99244;
    public const double Room102Longitude = -76.93750;
    // The two rooms' cameras stand on one line; DemoWorlds.cs places both pairs there.
    private const double RoomRowLatitude = 38.99240;

    // The top of the walls and the underside of the roof. Above the editor's cutaway (2.5 m), so the
    // rooms can be worked on from above, and above the cameras standing in them.
    public const float InteriorHeight = 3.6f;

    private const float WallThickness = 0.4f;
    private const float RoomWallThickness = 0.2f;
    private const float DoorWidth = 1.4f;
    private const string FloorColor = "#8f959d";
    private const string WallColor = "#c8ccd2";
    private const string RoofColor = "#6f747c";

    // A camera the demo places: ta_vms's camera id is the world's id behind the bench's prefix
    // (sim-master is the world's master). Height above the building floor, heading clockwise from
    // north, and the horizontal field of view: for a PTZ camera, the wide end of its lens.
    public sealed record DemoCamera(
        string Id, string Name, CameraKind Kind,
        double Latitude, double Longitude, float Height, float Heading, float HorizontalFov, float Pitch);

    public static readonly DemoCamera[] Cameras =
    [
        new("master", "Room 101 master", CameraKind.Fixed,
            RoomRowLatitude, -76.93852, 2.8f, 55, 70, -10),
        new("ptz", "Room 102 slave (PTZ)", CameraKind.Ptz,
            RoomRowLatitude, -76.93757, 2.8f, 50, 65, 0),
        new("master2", "Building master 2", CameraKind.Fixed,
            38.99276, -76.93895, 3f, 135, 80, -10),
        new("ptz2", "Campus slave 2 (PTZ)", CameraKind.Ptz,
            38.99250, -76.93950, 6f, 90, 60, 0),
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
        var (roomWestX, roomSouthZ) = Ground(world, Room101South, Room101West);
        var (roomEastX, roomNorthZ) = Ground(world, Room101North, Room101East);
        var (room102X, room102Z) = Ground(world, Room102Latitude, Room102Longitude);

        // The shell of the building: a floor slab, four walls and a roof slab.
        float cx = (eastX + westX) / 2, cz = (southZ + northZ) / 2;
        float width = eastX - westX, depth = southZ - northZ;
        // The floor's top sits just above the terrain: at exactly its height the two z-fight, and the
        // cameras see grass grow through the floor.
        Plate(world, "building-floor", cx, -0.25f, cz, width, 0.3f, depth, FloorColor);
        Plate(world, "building-roof", cx, InteriorHeight, cz, width, 0.35f, depth, RoofColor);
        Plate(world, "building-wall-n", cx, 0, northZ, width + WallThickness, InteriorHeight, WallThickness, WallColor);
        Plate(world, "building-wall-s", cx, 0, southZ, width + WallThickness, InteriorHeight, WallThickness, WallColor);
        Plate(world, "building-wall-w", westX, 0, cz, WallThickness, InteriorHeight, depth - WallThickness, WallColor);
        Plate(world, "building-wall-e", eastX, 0, cz, WallThickness, InteriorHeight, depth - WallThickness, WallColor);

        // Room 101 is the demo plan's rectangle. Room 102 has no rectangle of its own, so it takes
        // Room 101's size around its point.
        float room102WestX = room102X - (roomEastX - roomWestX) / 2;
        float room102EastX = room102X + (roomEastX - roomWestX) / 2;
        float room102NorthZ = room102Z - (roomSouthZ - roomNorthZ) / 2;
        float room102SouthZ = room102Z + (roomSouthZ - roomNorthZ) / 2;
        Room(world, "room-101", roomWestX, roomEastX, roomNorthZ, roomSouthZ);
        Room(world, "room-102", room102WestX, room102EastX, room102NorthZ, room102SouthZ);

        // The racks the demo's plan draws, as plain boxes: the simulator does not repeat the demo's
        // tree of switches and ports, it only stands something in the rooms to recognize them by.
        for (int i = 0; i < 4; i++)
            Plate(world, $"room-101-rack-{i + 1}", roomWestX + 3 + i * 3.2f, 0, roomNorthZ + 1.2f,
                0.6f, 2f, 1.2f, "#3d4149");
        for (int i = 0; i < 2; i++)
            Plate(world, $"room-102-rack-{i + 1}", room102WestX + 3 + i * 3.2f, 0, room102NorthZ + 1.2f,
                0.6f, 2f, 1.2f, "#3d4149");

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
        Plate(world, "parking", 80, 0, 142, 90, 0.06f, 24, "#5f6165");

        Add(world, "office-ne", "office", 165, 0, 145, 44, 11, 28, "#d0d4da", "#5c6068");
        Scenery(world);

        AddCameras(world);
        AddDevices(world);
        return world;
    }

    // One room: four walls, the south one with a door onto the hall.
    private static void Room(WorldModel world, string id, float westX, float eastX, float northZ, float southZ)
    {
        float width = eastX - westX, depth = southZ - northZ;
        float cx = (westX + eastX) / 2;
        Plate(world, $"{id}-wall-n", cx, 0, northZ, width + RoomWallThickness, InteriorHeight, RoomWallThickness, WallColor);
        Plate(world, $"{id}-wall-w", westX, 0, (northZ + southZ) / 2, RoomWallThickness, InteriorHeight, depth, WallColor);
        Plate(world, $"{id}-wall-e", eastX, 0, (northZ + southZ) / 2, RoomWallThickness, InteriorHeight, depth, WallColor);

        float door = westX + width / 2;
        float left = door - DoorWidth / 2 - westX, right = eastX - (door + DoorWidth / 2);
        if (left > 0)
            Plate(world, $"{id}-wall-s-1", westX + left / 2, 0, southZ, left, InteriorHeight, RoomWallThickness, WallColor);
        if (right > 0)
            Plate(world, $"{id}-wall-s-2", eastX - right / 2, 0, southZ, right, InteriorHeight, RoomWallThickness, WallColor);
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

    // The demo's relay and sensor: a lamp in Room 101 on the master's relay, a sensor in Room 102 on
    // the PTZ camera's input, and the wire between them — one camera's relay driving another's input.
    // Both cameras stand still: a wire to the car would trail across the world and make every camera
    // that sees it redraw at every frame of the drive, for no demonstration the static pair does not
    // already give.
    private static void AddDevices(WorldModel world)
    {
        var master = world.Cameras.Single(c => c.Id == "master");
        master.RelayOutputs.Add(new RelayOutputModel
        {
            Token = "lamp", Name = "Room lamp", Position = new Vec3(1.5f, 0, -1f),
        });
        var ptz = world.Cameras.Single(c => c.Id == "ptz");
        ptz.DigitalInputs.Add(new DigitalInputModel
        {
            Token = "input", Name = "Room sensor", Position = new Vec3(-1.5f, 0.2f, 1f),
        });
        world.Wires.Add(new WireModel
        {
            RelayCameraId = master.Id, RelayToken = "lamp",
            InputCameraId = ptz.Id, InputToken = "input",
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
