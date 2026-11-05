using Microsoft.Extensions.DependencyInjection;
using OnvifLib;
using OnvifSim.Onvif;
using OnvifSim.Rendering;
using OnvifSim.World;

namespace OnvifSim.Tests;

// Geolocation as an ONVIF client sees it, against the world the demo uses: Get reports where the
// camera stands in Maryland, Set moves a fixed camera there, and a camera bolted to the car refuses
// because the car decides where it is.
public class GeoLocationOnvifTests(MarylandFactory factory) : IClassFixture<MarylandFactory>
{
    private WorldModel World => factory.Services.GetRequiredService<WorldStore>().Current;

    private int PortOf(string cameraId) => factory.Services.GetRequiredService<OnvifHost>().PortOf(cameraId)
        ?? throw new InvalidOperationException($"{cameraId} is not served");

    private Camera Connect(string cameraId) => Camera.Create("127.0.0.1", PortOf(cameraId), "admin", "admin", timeout: 10);

    // The same pose the Device handler reports: the camera's mount, plus a PTZ head's own pan and
    // tilt, placed on the terrain.
    private static CameraPose PoseOf(WorldModel world, string cameraId)
    {
        var camera = world.Cameras.Single(c => c.Id == cameraId);
        return CameraPose.From(camera, world, DateTimeOffset.UtcNow, camera.Ptz?.Position ?? default);
    }

    private static GeoPosition GeoOf(WorldModel world, CameraPose pose) =>
        WorldGeo.ToGeo(world, new Vec3(pose.Position.X, pose.Position.Y, pose.Position.Z));

    [Theory]
    [InlineData("master")]
    [InlineData("ptz")]
    [InlineData("master2")]
    [InlineData("ptz2")]
    public async Task Get_ReportsThePoseOfAFixedCamera(string cameraId)
    {
        var world = World;
        var pose = PoseOf(world, cameraId);
        var expected = GeoOf(world, pose);

        var entity = Assert.Single(await Connect(cameraId).GetGeoLocationAsync());

        Assert.Equal("Device", entity.Entity);
        Assert.Equal(cameraId, entity.Token);
        Assert.True(entity.Fixed);
        Assert.NotNull(entity.Location);
        Assert.Equal(expected.Latitude, entity.Location!.Latitude!.Value, 6);
        Assert.Equal(expected.Longitude, entity.Location.Longitude!.Value, 6);
        Assert.Equal(expected.Elevation, entity.Location.ElevationMeters!.Value, 2);
        // The report is a true bearing: Maryland's north is 0, so it is the world heading.
        Assert.Equal(WorldGeo.TrueHeading(world, pose.Heading), entity.Orientation!.Yaw!.Value, 2);
    }

    [Fact]
    public async Task Get_ReportsAMovingCameraAsNotFixed()
    {
        var world = World;

        var entity = Assert.Single(await Connect(MarylandWorld.VehicleCameraId).GetGeoLocationAsync());

        Assert.False(entity.Fixed);
        Assert.NotNull(entity.Location);
        // The car drives around the building, so the camera is somewhere in the world and its place
        // is reported, not refused.
        var position = WorldGeo.ToWorld(world, entity.Location!.Latitude!.Value,
            entity.Location.Longitude!.Value, entity.Location.ElevationMeters ?? 0);
        Assert.InRange(Math.Abs(position.X), 0, world.Terrain.Size / 2);
        Assert.InRange(Math.Abs(position.Z), 0, world.Terrain.Size / 2);
    }

    [Fact]
    public async Task Set_MovesAFixedCameraToTheRequestedPlace()
    {
        var camera = Connect("master2");
        var original = Assert.Single(await camera.GetGeoLocationAsync());
        var world = World;
        var from = GeoOf(world, PoseOf(world, "master2"));
        // About ten metres north-east, inside the hall and at the camera's own height.
        var to = new OnvifGeoLocation(from.Latitude + 0.0001, from.Longitude + 0.0001, from.Elevation);

        try
        {
            await camera.SetGeoLocationAsync(new OnvifLocationEntity("Device", "master2", to));

            var moved = Assert.Single(await camera.GetGeoLocationAsync());
            Assert.Equal(to.Latitude!.Value, moved.Location!.Latitude!.Value, 6);
            Assert.Equal(to.Longitude!.Value, moved.Location.Longitude!.Value, 6);
            Assert.Equal(to.ElevationMeters!.Value, moved.Location.ElevationMeters!.Value, 1);
            Assert.True(moved.Fixed);
        }
        finally
        {
            // The other tests read Maryland's poses, so the world is put back as it was found.
            await camera.SetGeoLocationAsync(original);
        }
    }

    [Fact]
    public async Task Set_OutsideTheWorld_IsRefused()
    {
        // A degree north of the anchor is about 111 km away, far outside the terrain.
        var outside = new OnvifGeoLocation(MarylandWorld.Latitude + 1, MarylandWorld.Longitude, 3);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            Connect("master2").SetGeoLocationAsync(new OnvifLocationEntity("Device", "master2", outside)));
    }

    [Fact]
    public async Task Set_OnTheVehicleCamera_IsRefused()
    {
        var target = new OnvifGeoLocation(MarylandWorld.Latitude, MarylandWorld.Longitude, 2);

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            Connect(MarylandWorld.VehicleCameraId)
                .SetGeoLocationAsync(new OnvifLocationEntity("Device", MarylandWorld.VehicleCameraId, target)));

        // ActionNotSupported, not InvalidArg: the place is in the world, but the car decides it.
        Assert.Contains("vehicle", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
