namespace OnvifSim.World;

// Converts between the world's metres (X east, Y up, Z south) and WGS84 around its Geo anchor. The
// frame is the local equirectangular approximation ta_vms's EnuFrame uses, so a position this reports
// lands where that map expects it: exact to millimetres across a few hundred metres, about a metre at
// two kilometres.
public static class WorldGeo
{
    public const double EarthRadiusM = 6378137.0;

    public static GeoPosition ToGeo(WorldModel world, Vec3 position) => ToGeo(world.Geo, position);

    public static GeoPosition ToGeo(GeoModel geo, Vec3 position)
    {
        var (east, north) = EastNorth(geo, position.X, position.Z);
        var cosLat = Math.Max(Math.Abs(Math.Cos(geo.Latitude * Math.PI / 180)), 1e-9);
        return new GeoPosition(
            geo.Latitude + north / EarthRadiusM * 180 / Math.PI,
            geo.Longitude + east / (EarthRadiusM * cosLat) * 180 / Math.PI,
            geo.Elevation + position.Y);
    }

    public static Vec3 ToWorld(WorldModel world, double latitude, double longitude, double elevation)
    {
        var geo = world.Geo;
        var cosLat = Math.Max(Math.Abs(Math.Cos(geo.Latitude * Math.PI / 180)), 1e-9);
        var north = (latitude - geo.Latitude) * Math.PI / 180 * EarthRadiusM;
        var east = (longitude - geo.Longitude) * Math.PI / 180 * EarthRadiusM * cosLat;
        var (x, z) = FromEastNorth(geo, east, north);
        return new Vec3((float)x, (float)(elevation - geo.Elevation), (float)z);
    }

    // A camera's world heading is degrees clockwise from world -Z; its compass bearing also includes
    // where world -Z points on the Earth.
    public static float TrueHeading(WorldModel world, float heading) => TrueHeading(world.Geo, heading);

    public static float TrueHeading(GeoModel geo, float heading)
    {
        var bearing = (heading + geo.North) % 360f;
        return bearing < 0 ? bearing + 360f : bearing;
    }

    private static (double East, double North) EastNorth(GeoModel geo, double x, double z)
    {
        var (c, s) = Rotation(geo);
        return (x * c - z * s, -x * s - z * c);
    }

    private static (double X, double Z) FromEastNorth(GeoModel geo, double east, double north)
    {
        var (c, s) = Rotation(geo);
        return (east * c - north * s, -east * s - north * c);
    }

    private static (double Cos, double Sin) Rotation(GeoModel geo)
    {
        var theta = geo.North * Math.PI / 180;
        return (Math.Cos(theta), Math.Sin(theta));
    }
}

// A WGS84 point: degrees, and metres above sea level.
public readonly record struct GeoPosition(double Latitude, double Longitude, double Elevation);
