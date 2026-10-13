using OnvifSim.Onvif.Soap;
using OnvifSim.Onvif.Wsdl;
using OnvifSim.Onvif.Wsdl.Imaging;
using OnvifSim.World;
using static OnvifSim.Onvif.Services.CameraMedia;

namespace OnvifSim.Onvif.Services;

// Imaging (ver20), the minimum: brightness, contrast and colour saturation of the one video source,
// applied to the rendered picture and saved to world.json. Focus is fixed, so there is nothing to move.
[OnvifService(typeof(ImagingPort), "imaging_service", 21, 12)]
public sealed class ImagingHandlers(OnvifRequestContext context)
{
    private const float Min = 0;
    private const float Max = 100;

    // Capabilities4 is timg:Capabilities: svcutil numbers classes whose names other namespaces took.
    public GetServiceCapabilitiesResponse GetServiceCapabilities(GetServiceCapabilitiesRequest request) => new()
    {
        Capabilities = new Capabilities4
        {
            ImageStabilization = false, ImageStabilizationSpecified = true,
            Presets = false, PresetsSpecified = true,
        },
    };

    public GetImagingSettingsResponse GetImagingSettings(GetImagingSettingsRequest request)
    {
        CheckSource(request.VideoSourceToken);
        var imaging = context.Camera.Imaging;
        return new GetImagingSettingsResponse
        {
            ImagingSettings = new ImagingSettings20
            {
                Brightness = imaging.Brightness, BrightnessSpecified = true,
                Contrast = imaging.Contrast, ContrastSpecified = true,
                ColorSaturation = imaging.ColorSaturation, ColorSaturationSpecified = true,
            },
        };
    }

    // Settings the camera does not have (exposure, focus, white balance...) are ignored rather than
    // refused, so a client writing back everything it read still works.
    public SetImagingSettingsResponse SetImagingSettings(SetImagingSettingsRequest request)
    {
        CheckSource(request.VideoSourceToken);
        var settings = request.ImagingSettings ?? throw OnvifFault.SettingsInvalid("The request has no settings.");
        float? brightness = Checked(settings.BrightnessSpecified, settings.Brightness, "Brightness");
        float? contrast = Checked(settings.ContrastSpecified, settings.Contrast, "Contrast");
        float? saturation = Checked(settings.ColorSaturationSpecified, settings.ColorSaturation, "ColorSaturation");
        context.Store.Update(world =>
        {
            var imaging = world.Cameras.Single(c => c.Id == context.Camera.Id).Imaging;
            imaging.Brightness = brightness ?? imaging.Brightness;
            imaging.Contrast = contrast ?? imaging.Contrast;
            imaging.ColorSaturation = saturation ?? imaging.ColorSaturation;
        });
        return new SetImagingSettingsResponse();
    }

    public GetOptionsResponse GetOptions(GetOptionsRequest request)
    {
        CheckSource(request.VideoSourceToken);
        return new GetOptionsResponse
        {
            ImagingOptions = new ImagingOptions20
            {
                Brightness = new FloatRange { Min = Min, Max = Max },
                Contrast = new FloatRange { Min = Min, Max = Max },
                ColorSaturation = new FloatRange { Min = Min, Max = Max },
            },
        };
    }

    // No focus to drive: no absolute, relative or continuous focus options.
    public GetMoveOptionsResponse GetMoveOptions(GetMoveOptionsRequest request)
    {
        CheckSource(request.VideoSourceToken);
        return new GetMoveOptionsResponse { MoveOptions = new MoveOptions20() };
    }

    public GetStatusResponse GetStatus(GetStatusRequest request)
    {
        CheckSource(request.VideoSourceToken);
        return new GetStatusResponse { Status = new ImagingStatus20() };
    }

    private static void CheckSource(string? token)
    {
        if (token != VideoSourceToken) throw OnvifFault.NoSource(token ?? "");
    }

    private static float? Checked(bool specified, float value, string name)
    {
        if (!specified) return null;
        return value is >= Min and <= Max ? value : throw OnvifFault.SettingsInvalid($"{name} {value} is outside {Min}..{Max}.");
    }
}
