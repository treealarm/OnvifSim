using OnvifSim.Onvif.Wsdl;
using OnvifSim.World;

namespace OnvifSim.Onvif.Services;

// The PTZ head of a simulated camera as ONVIF sees it: one node, one configuration in the camera's one
// profile, and the generic spaces only. Generic positions map linearly onto the camera's limits in
// degrees (pan -1..1 onto PanMin..PanMax, tilt -1..1 onto TiltMin..TiltMax) and zoom 0..1 is the
// simulator's own zoom; an endless pan maps -1..1 onto one full turn starting at PanMin.
public static class CameraPtz
{
    public const string NodeToken = "PTZNode_1";
    public const string ConfigurationToken = "PTZConfig_1";
    public const int MaximumNumberOfPresets = 32;
    public const int MaximumPresetNameLength = 64;

    // How long a ContinuousMove without a timeout runs, and the timeouts a client may ask for.
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MinTimeout = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(600);

    private const string Spaces = "http://www.onvif.org/ver10/tptz/";
    public const string PanTiltPositionSpace = Spaces + "PanTiltSpaces/PositionGenericSpace";
    public const string ZoomPositionSpace = Spaces + "ZoomSpaces/PositionGenericSpace";
    public const string PanTiltTranslationSpace = Spaces + "PanTiltSpaces/TranslationGenericSpace";
    public const string ZoomTranslationSpace = Spaces + "ZoomSpaces/TranslationGenericSpace";
    public const string PanTiltVelocitySpace = Spaces + "PanTiltSpaces/VelocityGenericSpace";
    public const string ZoomVelocitySpace = Spaces + "ZoomSpaces/VelocityGenericSpace";
    public const string PanTiltSpeedSpace = Spaces + "PanTiltSpaces/GenericSpeedSpace";
    public const string ZoomSpeedSpace = Spaces + "ZoomSpaces/ZoomGenericSpeedSpace";

    public static PTZNode Node() => new()
    {
        token = NodeToken,
        Name = "PTZ",
        SupportedPTZSpaces = SupportedSpaces(),
        MaximumNumberOfPresets = MaximumNumberOfPresets,
        HomeSupported = true,
        FixedHomePosition = false, FixedHomePositionSpecified = true,
        GeoMove = false, GeoMoveSpecified = true,
    };

    public static PTZConfiguration Configuration() => new()
    {
        token = ConfigurationToken,
        Name = "PTZ",
        UseCount = 1,
        NodeToken = NodeToken,
        DefaultAbsolutePantTiltPositionSpace = PanTiltPositionSpace,
        DefaultAbsoluteZoomPositionSpace = ZoomPositionSpace,
        DefaultRelativePanTiltTranslationSpace = PanTiltTranslationSpace,
        DefaultRelativeZoomTranslationSpace = ZoomTranslationSpace,
        DefaultContinuousPanTiltVelocitySpace = PanTiltVelocitySpace,
        DefaultContinuousZoomVelocitySpace = ZoomVelocitySpace,
        DefaultPTZSpeed = new PTZSpeed
        {
            PanTilt = new Vector2D { x = 1, y = 1, space = PanTiltSpeedSpace },
            Zoom = new Vector1D { x = 1, space = ZoomSpeedSpace },
        },
        DefaultPTZTimeout = System.Xml.XmlConvert.ToString(DefaultTimeout),
        PanTiltLimits = new PanTiltLimits { Range = Range2D(PanTiltPositionSpace, -1, 1) },
        ZoomLimits = new ZoomLimits { Range = Range1D(ZoomPositionSpace, 0, 1) },
    };

    public static PTZSpaces SupportedSpaces() => new()
    {
        AbsolutePanTiltPositionSpace = [Range2D(PanTiltPositionSpace, -1, 1)],
        AbsoluteZoomPositionSpace = [Range1D(ZoomPositionSpace, 0, 1)],
        RelativePanTiltTranslationSpace = [Range2D(PanTiltTranslationSpace, -1, 1)],
        RelativeZoomTranslationSpace = [Range1D(ZoomTranslationSpace, -1, 1)],
        ContinuousPanTiltVelocitySpace = [Range2D(PanTiltVelocitySpace, -1, 1)],
        ContinuousZoomVelocitySpace = [Range1D(ZoomVelocitySpace, -1, 1)],
        PanTiltSpeedSpace = [Range1D(PanTiltSpeedSpace, 0, 1)],
        ZoomSpeedSpace = [Range1D(ZoomSpeedSpace, 0, 1)],
    };

    public static PTZVector ToGeneric(PtzModel ptz, PtzPosition position) => new()
    {
        PanTilt = new Vector2D
        {
            x = (position.Pan - ptz.PanMin) / PanSpan(ptz) * 2 - 1,
            y = (position.Tilt - ptz.TiltMin) / (ptz.TiltMax - ptz.TiltMin) * 2 - 1,
            space = PanTiltPositionSpace,
        },
        Zoom = new Vector1D { x = position.Zoom, space = ZoomPositionSpace },
    };

    public static float PanFromGeneric(PtzModel ptz, float x) => ptz.PanMin + (x + 1) / 2 * PanSpan(ptz);

    public static float TiltFromGeneric(PtzModel ptz, float y) => ptz.TiltMin + (y + 1) / 2 * (ptz.TiltMax - ptz.TiltMin);

    // A generic translation of 1 is the whole range of the axis (ONVIF PTZ: "the translation range of
    // the PTZ unit normalized to the range -1 to 1").
    public static float PanStepFromGeneric(PtzModel ptz, float x) => x * PanSpan(ptz);

    public static float TiltStepFromGeneric(PtzModel ptz, float y) => y * (ptz.TiltMax - ptz.TiltMin);

    private static float PanSpan(PtzModel ptz) => ptz.EndlessPan ? 360 : ptz.PanMax - ptz.PanMin;

    private static Space2DDescription Range2D(string uri, float min, float max) => new()
    {
        URI = uri,
        XRange = new FloatRange { Min = min, Max = max },
        YRange = new FloatRange { Min = min, Max = max },
    };

    private static Space1DDescription Range1D(string uri, float min, float max) => new()
    {
        URI = uri,
        XRange = new FloatRange { Min = min, Max = max },
    };
}
