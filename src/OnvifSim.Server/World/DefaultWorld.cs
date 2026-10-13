namespace OnvifSim.World;

// The world a fresh install starts with: rolling hills, a small village, a pine wood and two cameras,
// so every endpoint has something to show before anyone opens the editor.
public static class DefaultWorld
{
    public static WorldModel Create()
    {
        var world = new WorldModel();
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
            Ptz = new PtzModel { Tilt = -10 },
            Users = [new UserModel { Username = "admin", Password = "admin" }],
        });
        return world;
    }
}
