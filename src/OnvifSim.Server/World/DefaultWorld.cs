namespace OnvifSim.World;

// The world a fresh install starts with: rolling hills, a small village, a pine wood and three cameras
// (one of them on the driving car), so every endpoint has something to show before anyone opens the
// editor.
public static class DefaultWorld
{
    // A world with nothing in it: flat ground, no cameras, no objects. What "New world" opens.
    public static WorldModel CreateEmpty() => new();

    public static WorldModel Create()
    {
        var world = new WorldModel
        {
            // The harness's old anchor, in Maryland farmland: a latitude far enough from the equator
            // that a forgotten cos(latitude) shows up as an east-west error.
            Geo = new GeoModel { Latitude = 39.2, Longitude = -77.35, Elevation = 0, North = 0 },
        };
        var t = world.Terrain;
        int n = t.Resolution + 1;
        t.Heights = new float[n * n];
        for (int iz = 0; iz < n; iz++)
            for (int ix = 0; ix < n; ix++)
            {
                float x = ix * t.Size / t.Resolution - t.Size / 2;
                float z = iz * t.Size / t.Resolution - t.Size / 2;
                // Low hills that rise towards the north edge, flat enough near the centre for houses.
                float r = MathF.Sqrt(x * x + z * z);
                float hills = 6 * MathF.Sin(x * 0.021f) * MathF.Cos(z * 0.017f) + 3 * MathF.Sin(x * 0.05f + z * 0.043f);
                float flat = Math.Clamp((r - 40) / 80, 0, 1);
                float ridge = Math.Clamp(-z / 200, 0, 1) * 18;
                t.Heights[iz * n + ix] = (hills + ridge) * flat;
            }

        var rng = new Random(7);
        int id = 0;
        void Add(string type, float x, float z, Vec3 scale, float rot = 0, string? c1 = null, string? c2 = null) =>
            world.Objects.Add(new WorldObject
            {
                Id = $"o{++id}", Type = type, Position = new(x, 0, z), Scale = scale, RotationY = rot,
                Color = c1, Color2 = c2, Seed = rng.Next(),
            });

        // Village around the centre.
        (float x, float z, float rot)[] houses = [(-20, -10, 0), (5, -18, 15), (22, -4, 90), (-8, 16, 180), (18, 20, 200), (-28, 12, 270)];
        string[] walls = ["#d8c9a8", "#e6e0d4", "#b5c4b1", "#d9b8a0"];
        string[] roofs = ["#8a3b2e", "#5a4a42", "#3e5468", "#7a5230"];
        for (int i = 0; i < houses.Length; i++)
            Add("house", houses[i].x, houses[i].z, new(8 + i % 3 * 2, 5 + i % 2, 10), houses[i].rot, walls[i % walls.Length], roofs[i % roofs.Length]);

        // A pine wood to the north-west, scattered oaks elsewhere, a few rocks.
        for (int i = 0; i < 70; i++)
        {
            float x = -150 + (float)rng.NextDouble() * 110, z = -160 + (float)rng.NextDouble() * 110;
            float h = 9 + (float)rng.NextDouble() * 7;
            Add("tree.pine", x, z, new(h * 0.35f, h, h * 0.35f));
        }
        for (int i = 0; i < 35; i++)
        {
            float a = (float)(rng.NextDouble() * Math.PI * 2), r = 45 + (float)rng.NextDouble() * 120;
            float h = 6 + (float)rng.NextDouble() * 5;
            Add("tree.oak", MathF.Cos(a) * r, MathF.Sin(a) * r, new(h * 0.7f, h, h * 0.7f));
        }
        for (int i = 0; i < 12; i++)
        {
            float s = 1 + (float)rng.NextDouble() * 2.5f;
            Add("rock", -100 + (float)rng.NextDouble() * 200, -100 + (float)rng.NextDouble() * 200, new(s * 1.3f, s, s));
        }
        // A bright target box, handy for checking where a PTZ camera points.
        Add("box", 0, -45, new(3, 3, 3), 0, "#e8452c");

        // A parked car in the foreground of the village camera, facing it, so a fresh install shows the
        // car and its plate feature. Far enough not to hide the walkers behind it.
        Add("car", 10, 42, new(1.8f, 1.45f, 4.4f), 180, "#b03a30", "#f0f0ea");
        world.Objects[^1].Plate = "A123BC";

        // A road through the village between the two rows of houses, and people walking along it: one
        // down the middle, one on the south side the other way, one the whole length of the village.
        world.Roads.Add(new RoadModel
        {
            Id = "r1", Width = 5,
            Points = [new(-160, 40), new(-70, 8), new(0, 5), new(70, 3), new(160, -30)],
        });
        world.Walkers.Add(new WalkerModel
        {
            Id = "w1", Return = RouteReturn.Reverse,
            Route = [new(-45, 6), new(0, 4.5f), new(45, 3.5f)],
        });
        world.Walkers.Add(new WalkerModel
        {
            Id = "w2", Return = RouteReturn.Reverse, Speed = 1.6f, Height = 1.8f, Color = "#d9c27a", Color2 = "#4a3b2a",
            Route = [new(50, 4.6f), new(0, 6), new(-50, 8.1f)],
        });
        world.Walkers.Add(new WalkerModel
        {
            Id = "w3", Return = RouteReturn.Reverse, Speed = 1.1f, Height = 1.65f, Color = "#a83b3b", Color2 = "#2f4a6b",
            Route = [new(-90, 14.1f), new(-70, 7), new(0, 4), new(70, 2), new(110, -12.7f)],
        });
        // A car driving the same road, in the right-hand lane, so the walkers and a moving vehicle
        // share it in a fresh install. The lane is the road's points pushed sideways by the width of
        // half a lane.
        world.Vehicles.Add(new VehicleModel
        {
            Id = "v1", Return = RouteReturn.Reverse, Speed = 7,
            Color = "#2f6fde", Color2 = "#f0f0ea", Plate = "M777MM",
            Route = [new(-159.67f, 41.36f), new(-69.74f, 9.36f), new(0.05f, 6.4f), new(70.26f, 4.36f), new(160.48f, -28.69f)],
        });

        world.Cameras.Add(new CameraModel
        {
            Id = "cam1", Name = "Village fixed", Kind = CameraKind.Fixed,
            Position = new(0, 6, 60), Heading = 0, Pitch = -8, HFov = 75,
            Users = [new UserModel { Username = "admin", Password = "admin" }],
        });
        world.Cameras.Add(new CameraModel
        {
            Id = "cam2", Name = "Tower PTZ", Kind = CameraKind.Ptz,
            Position = new(40, 15, 40), Heading = 315, Pitch = 0,
            Ptz = new PtzModel { Tilt = -10, Home = new PtzPosition(0, -10, 0) },
            AngleGrid = true,
            Users = [new UserModel { Username = "admin", Password = "admin" }],
        });
        // A camera bolted to the car: its position is the mount's offset on the car, not a place on
        // the ground, so its picture moves with the car.
        world.Cameras.Add(new CameraModel
        {
            Id = "cam5", Name = "Chase camera", Kind = CameraKind.Fixed,
            MountedOnVehicleId = "v1",
            Position = new(0, 2.2f, 0.5f), Heading = 0, Pitch = -5, HFov = 70,
            Users = [new UserModel { Username = "admin", Password = "admin" }],
        });
        return world;
    }
}
