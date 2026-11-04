using System.Text.Json;
using OnvifSim.Editor;
using OnvifSim.World;

namespace OnvifSim.Tests;

public class WorldGeoTests
{
    private static WorldModel World(double latitude = 39.2, double longitude = -77.35, float north = 0) =>
        new() { Geo = new GeoModel { Latitude = latitude, Longitude = longitude, Elevation = 10, North = north } };

    private static double MetresToLatitude(double metres) => metres / WorldGeo.EarthRadiusM * 180 / Math.PI;

    private static double MetresToLongitude(double metres, double latitude) =>
        metres / (WorldGeo.EarthRadiusM * Math.Cos(latitude * Math.PI / 180)) * 180 / Math.PI;

    [Fact]
    public void TheOrigin_IsTheConfiguredPoint()
    {
        var geo = WorldGeo.ToGeo(World(), new Vec3(0, 0, 0));
        Assert.Equal(39.2, geo.Latitude, 9);
        Assert.Equal(-77.35, geo.Longitude, 9);
        Assert.Equal(10, geo.Elevation, 3);
    }

    [Fact]
    public void ANorthwardMetre_IsALatitudeStep()
    {
        // World -Z is north when North is 0.
        var geo = WorldGeo.ToGeo(World(), new Vec3(0, 2.5f, -1));
        Assert.Equal(39.2 + MetresToLatitude(1), geo.Latitude, 9);
        Assert.Equal(-77.35, geo.Longitude, 9);
        Assert.Equal(12.5, geo.Elevation, 3);
    }

    [Fact]
    public void AnEastwardMetre_IsALongitudeStepAtTheLatitude()
    {
        var geo = WorldGeo.ToGeo(World(), new Vec3(1, 0, 0));
        Assert.Equal(-77.35 + MetresToLongitude(1, 39.2), geo.Longitude, 9);
        Assert.Equal(39.2, geo.Latitude, 9);
    }

    [Fact]
    public void North_RotatesTheWorldFrame()
    {
        // With north set to 90 degrees, world -Z points east and world +X points south.
        var world = World(north: 90);

        var northInWorld = WorldGeo.ToGeo(world, new Vec3(0, 0, -10));
        Assert.Equal(-77.35 + MetresToLongitude(10, 39.2), northInWorld.Longitude, 9);
        Assert.Equal(39.2, northInWorld.Latitude, 9);

        var eastInWorld = WorldGeo.ToGeo(world, new Vec3(10, 0, 0));
        Assert.Equal(39.2 - MetresToLatitude(10), eastInWorld.Latitude, 9);
        Assert.Equal(-77.35, eastInWorld.Longitude, 9);
    }

    [Fact]
    public void ToWorld_IsTheInverseOfToGeo()
    {
        var world = World(north: 37);
        var position = new Vec3(12.5f, 3.25f, -7.75f);
        var geo = WorldGeo.ToGeo(world, position);
        var back = WorldGeo.ToWorld(world, geo.Latitude, geo.Longitude, geo.Elevation);
        Assert.Equal(position.X, back.X, 2);
        Assert.Equal(position.Y, back.Y, 2);
        Assert.Equal(position.Z, back.Z, 2);
    }

    [Fact]
    public void TrueHeading_AddsTheWorldsNorth_AndWraps()
    {
        Assert.Equal(100, WorldGeo.TrueHeading(World(north: 90), 10), 3);
        Assert.Equal(10, WorldGeo.TrueHeading(World(north: 350), 20), 3);
    }

    [Fact]
    public void AWorldFileWithoutGeo_ReadsAsTheEquator()
    {
        var world = JsonSerializer.Deserialize<WorldModel>(
            """{"terrain":{"size":10,"resolution":1,"heights":[0,1,2,3]}}""", WorldStore.Json)!;
        Assert.Equal(0, world.Geo.Latitude);
        Assert.Equal(0, world.Geo.Longitude);
        Assert.Equal(0, world.Geo.North);
    }

    [Fact]
    public void AMountedCamera_NeedsItsVehicle()
    {
        var world = DefaultWorld.Create();
        world.Cameras.Single(c => c.Id == "cam5").MountedOnVehicleId = "missing";
        Assert.Throws<InvalidWorldException>(() => WorldValidation.Check(world, onvifBasePort: 8001));
    }

    [Theory]
    [InlineData(91, 0, 0, 0)]
    [InlineData(0, 181, 0, 0)]
    [InlineData(0, 0, 0, 361)]
    [InlineData(0, 0, float.NaN, 0)]
    public void AnInvalidGeoOrigin_IsRefused(double latitude, double longitude, float elevation, float north)
    {
        var world = new WorldModel
        {
            Geo = new GeoModel { Latitude = latitude, Longitude = longitude, Elevation = elevation, North = north },
        };
        Assert.Throws<InvalidWorldException>(() => WorldValidation.Check(world, onvifBasePort: 8001));
    }
}
