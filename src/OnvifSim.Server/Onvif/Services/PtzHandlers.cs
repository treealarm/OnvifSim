using System.Xml;
using OnvifSim.Onvif.Soap;
using OnvifSim.Onvif.Wsdl;
using OnvifSim.Onvif.Wsdl.Ptz;
using OnvifSim.Ptz;
using OnvifSim.World;
using static OnvifSim.Onvif.Services.CameraPtz;

namespace OnvifSim.Onvif.Services;

// PTZ (ver20) of a PTZ camera: absolute, relative and continuous moves in the generic spaces, status,
// presets and a home position. Moves run in the PtzSimulator; presets and home are saved to world.json.
[OnvifService(typeof(PTZ), "ptz_service", 21, 12, RequiresPtz = true)]
public sealed class PtzHandlers(OnvifRequestContext context)
{
    private CameraModel Camera => context.Camera;
    private PtzModel Ptz => context.Camera.Ptz!;

    // Capabilities3 is tptz:Capabilities: svcutil numbers classes whose names other namespaces took.
    public GetServiceCapabilitiesResponse GetServiceCapabilities(GetServiceCapabilitiesRequest request) => new()
    {
        Capabilities = new Capabilities3
        {
            EFlip = false, EFlipSpecified = true,
            Reverse = false, ReverseSpecified = true,
            GetCompatibleConfigurations = true, GetCompatibleConfigurationsSpecified = true,
            MoveStatus = true, MoveStatusSpecified = true,
            StatusPosition = true, StatusPositionSpecified = true,
        },
    };

    public GetNodesResponse GetNodes(GetNodesRequest request) => new() { PTZNode = [Node()] };

    public GetNodeResponse GetNode(GetNodeRequest request) =>
        request.NodeToken == NodeToken ? new GetNodeResponse { PTZNode = Node() } : throw OnvifFault.NoEntity(request.NodeToken);

    public GetConfigurationsResponse GetConfigurations(GetConfigurationsRequest request) => new() { PTZConfiguration = [Configuration()] };

    public GetConfigurationResponse GetConfiguration(GetConfigurationRequest request) =>
        request.PTZConfigurationToken == ConfigurationToken
            ? new GetConfigurationResponse { PTZConfiguration = Configuration() }
            : throw OnvifFault.NoConfig(request.PTZConfigurationToken);

    public GetCompatibleConfigurationsResponse GetCompatibleConfigurations(GetCompatibleConfigurationsRequest request)
    {
        RequireProfile(request.ProfileToken);
        return new GetCompatibleConfigurationsResponse { PTZConfiguration = [Configuration()] };
    }

    public GetConfigurationOptionsResponse GetConfigurationOptions(GetConfigurationOptionsRequest request)
    {
        if (request.ConfigurationToken != ConfigurationToken) throw OnvifFault.NoConfig(request.ConfigurationToken);
        return new GetConfigurationOptionsResponse
        {
            PTZConfigurationOptions = new PTZConfigurationOptions
            {
                Spaces = SupportedSpaces(),
                PTZTimeout = new DurationRange { Min = XmlConvert.ToString(MinTimeout), Max = XmlConvert.ToString(MaxTimeout) },
            },
        };
    }

    public AbsoluteMoveResponse AbsoluteMove(AbsoluteMoveRequest request)
    {
        RequireProfile(request.ProfileToken);
        var position = request.Position ?? throw OnvifFault.InvalidPosition("The request has no position.");
        float? pan = null, tilt = null, zoom = null;
        if (position.PanTilt is { } panTilt)
        {
            CheckSpace(panTilt.space, PanTiltPositionSpace);
            if (!InRange(panTilt.x, -1, 1) || !InRange(panTilt.y, -1, 1))
                throw OnvifFault.InvalidPosition($"Pan/tilt ({panTilt.x}, {panTilt.y}) is outside -1..1.");
            pan = PanFromGeneric(Ptz, panTilt.x);
            tilt = TiltFromGeneric(Ptz, panTilt.y);
        }
        if (position.Zoom is { } zoomPosition)
        {
            CheckSpace(zoomPosition.space, ZoomPositionSpace);
            if (!InRange(zoomPosition.x, 0, 1)) throw OnvifFault.InvalidPosition($"Zoom {zoomPosition.x} is outside 0..1.");
            zoom = zoomPosition.x;
        }
        context.Ptz.MoveTo(Camera, new PtzAxes(pan, tilt, zoom), Speed(request.Speed));
        return new AbsoluteMoveResponse();
    }

    public RelativeMoveResponse RelativeMove(RelativeMoveRequest request)
    {
        RequireProfile(request.ProfileToken);
        var translation = request.Translation ?? throw OnvifFault.InvalidTranslation("The request has no translation.");
        float? pan = null, tilt = null, zoom = null;
        if (translation.PanTilt is { } panTilt)
        {
            CheckSpace(panTilt.space, PanTiltTranslationSpace);
            if (!InRange(panTilt.x, -1, 1) || !InRange(panTilt.y, -1, 1))
                throw OnvifFault.InvalidTranslation($"Pan/tilt ({panTilt.x}, {panTilt.y}) is outside -1..1.");
            pan = PanStepFromGeneric(Ptz, panTilt.x);
            tilt = TiltStepFromGeneric(Ptz, panTilt.y);
        }
        if (translation.Zoom is { } zoomStep)
        {
            CheckSpace(zoomStep.space, ZoomTranslationSpace);
            if (!InRange(zoomStep.x, -1, 1)) throw OnvifFault.InvalidTranslation($"Zoom {zoomStep.x} is outside -1..1.");
            zoom = zoomStep.x;
        }
        context.Ptz.MoveBy(Camera, new PtzAxes(pan, tilt, zoom), Speed(request.Speed));
        return new RelativeMoveResponse();
    }

    public ContinuousMoveResponse ContinuousMove(ContinuousMoveRequest request)
    {
        RequireProfile(request.ProfileToken);
        var velocity = request.Velocity ?? throw OnvifFault.InvalidVelocity("The request has no velocity.");
        float? pan = null, tilt = null, zoom = null;
        if (velocity.PanTilt is { } panTilt)
        {
            CheckSpace(panTilt.space, PanTiltVelocitySpace);
            if (!InRange(panTilt.x, -1, 1) || !InRange(panTilt.y, -1, 1))
                throw OnvifFault.InvalidVelocity($"Pan/tilt velocity ({panTilt.x}, {panTilt.y}) is outside -1..1.");
            (pan, tilt) = (panTilt.x, panTilt.y);
        }
        if (velocity.Zoom is { } zoomVelocity)
        {
            CheckSpace(zoomVelocity.space, ZoomVelocitySpace);
            if (!InRange(zoomVelocity.x, -1, 1)) throw OnvifFault.InvalidVelocity($"Zoom velocity {zoomVelocity.x} is outside -1..1.");
            zoom = zoomVelocity.x;
        }
        context.Ptz.MoveContinuously(Camera, new PtzAxes(pan, tilt, zoom), Timeout(request.Timeout));
        return new ContinuousMoveResponse();
    }

    // PanTilt and Zoom are optional booleans that the generated message cannot tell from false. Both
    // false is "stop nothing", which nobody asks for; it is how a Stop without arguments arrives, and
    // that one stops everything (ONVIF PTZ: "If no stop filter arguments are present...").
    public StopResponse Stop(StopRequest request)
    {
        RequireProfile(request.ProfileToken);
        bool all = !request.PanTilt && !request.Zoom;
        context.Ptz.Stop(Camera, all || request.PanTilt, all || request.Zoom);
        return new StopResponse();
    }

    public GetStatusResponse GetStatus(GetStatusRequest request)
    {
        RequireProfile(request.ProfileToken);
        var state = context.Ptz.StateOf(Camera);
        return new GetStatusResponse
        {
            PTZStatus = new PTZStatus
            {
                Position = ToGeneric(Ptz, state.Position),
                MoveStatus = new PTZMoveStatus
                {
                    PanTilt = state.PanTiltMoving ? MoveStatus.MOVING : MoveStatus.IDLE, PanTiltSpecified = true,
                    Zoom = state.ZoomMoving ? MoveStatus.MOVING : MoveStatus.IDLE, ZoomSpecified = true,
                },
                UtcTime = System.DateTime.UtcNow,
            },
        };
    }

    public GetPresetsResponse GetPresets(GetPresetsRequest request)
    {
        RequireProfile(request.ProfileToken);
        return new GetPresetsResponse
        {
            Preset = Ptz.Presets.Select(p => new PTZPreset { token = p.Token, Name = p.Name, PTZPosition = ToGeneric(Ptz, p.Position) }).ToArray(),
        };
    }

    // Saves where the head is now, as a new preset or over an existing one. Checked against the world
    // inside the update, so two clients saving at once cannot exceed the limit or share a name.
    public SetPresetResponse SetPreset(SetPresetRequest request)
    {
        RequireProfile(request.ProfileToken);
        string? name = string.IsNullOrWhiteSpace(request.PresetName) ? null : request.PresetName.Trim();
        if (name is { Length: > MaximumPresetNameLength })
            throw OnvifFault.InvalidPresetName($"A preset name has at most {MaximumPresetNameLength} characters.");
        var position = context.Ptz.PositionOf(Camera);
        string token = "";
        context.Store.Update(world =>
        {
            var presets = PtzOf(world).Presets;
            var preset = string.IsNullOrEmpty(request.PresetToken)
                ? AddPreset(presets)
                : presets.FirstOrDefault(p => p.Token == request.PresetToken) ?? throw OnvifFault.NoToken(request.PresetToken);
            if (name != null)
            {
                if (presets.Any(p => p != preset && p.Name == name)) throw OnvifFault.PresetExist(name);
                preset.Name = name;
            }
            preset.Position = position;
            token = preset.Token;
        });
        return new SetPresetResponse { PresetToken = token };
    }

    public GotoPresetResponse GotoPreset(GotoPresetRequest request)
    {
        RequireProfile(request.ProfileToken);
        var preset = Ptz.Presets.FirstOrDefault(p => p.Token == request.PresetToken) ?? throw OnvifFault.NoToken(request.PresetToken);
        MoveTo(preset.Position, request.Speed);
        return new GotoPresetResponse();
    }

    public RemovePresetResponse RemovePreset(RemovePresetRequest request)
    {
        RequireProfile(request.ProfileToken);
        context.Store.Update(world =>
        {
            if (PtzOf(world).Presets.RemoveAll(p => p.Token == request.PresetToken) == 0)
                throw OnvifFault.NoToken(request.PresetToken);
        });
        return new RemovePresetResponse();
    }

    public GotoHomePositionResponse GotoHomePosition(GotoHomePositionRequest request)
    {
        RequireProfile(request.ProfileToken);
        MoveTo(Ptz.Home, request.Speed);
        return new GotoHomePositionResponse();
    }

    public SetHomePositionResponse SetHomePosition(SetHomePositionRequest request)
    {
        RequireProfile(request.ProfileToken);
        var position = context.Ptz.PositionOf(Camera);
        context.Store.Update(world => PtzOf(world).Home = position);
        return new SetHomePositionResponse();
    }

    private void MoveTo(PtzPosition position, PTZSpeed? speed) =>
        context.Ptz.MoveTo(Camera, new PtzAxes(position.Pan, position.Tilt, position.Zoom), Speed(speed));

    private PtzModel PtzOf(WorldModel world) => world.Cameras.Single(c => c.Id == Camera.Id).Ptz!;

    // A new preset under the first free token "Preset_N", named "Preset N" until the client names it.
    private static PtzPresetModel AddPreset(List<PtzPresetModel> presets)
    {
        if (presets.Count >= MaximumNumberOfPresets) throw OnvifFault.TooManyPresets(MaximumNumberOfPresets);
        int number = Enumerable.Range(1, MaximumNumberOfPresets).First(n => presets.All(p => p.Token != $"Preset_{n}"));
        var preset = new PtzPresetModel { Token = $"Preset_{number}", Name = $"Preset {number}" };
        presets.Add(preset);
        return preset;
    }

    // Speeds are fractions of the top speed; absent axes move at the default, full speed.
    private static PtzAxes Speed(PTZSpeed? speed)
    {
        float? pan = null, tilt = null, zoom = null;
        if (speed?.PanTilt is { } panTilt)
        {
            CheckSpace(panTilt.space, PanTiltSpeedSpace);
            if (!InRange(panTilt.x, 0, 1) || !InRange(panTilt.y, 0, 1))
                throw OnvifFault.InvalidSpeed($"Pan/tilt speed ({panTilt.x}, {panTilt.y}) is outside 0..1.");
            (pan, tilt) = (panTilt.x, panTilt.y);
        }
        if (speed?.Zoom is { } zoomSpeed)
        {
            CheckSpace(zoomSpeed.space, ZoomSpeedSpace);
            if (!InRange(zoomSpeed.x, 0, 1)) throw OnvifFault.InvalidSpeed($"Zoom speed {zoomSpeed.x} is outside 0..1.");
            zoom = zoomSpeed.x;
        }
        return new PtzAxes(pan, tilt, zoom);
    }

    // Longer timeouts than the options offer are shortened rather than refused: clients that ask for
    // "until Stop" with a large value still get a moving camera.
    private static TimeSpan Timeout(string? timeout)
    {
        if (string.IsNullOrEmpty(timeout)) return DefaultTimeout;
        TimeSpan value;
        try
        {
            value = XmlConvert.ToTimeSpan(timeout);
        }
        catch (FormatException)
        {
            throw OnvifFault.InvalidArgValue($"Timeout '{timeout}' is not an xs:duration.");
        }
        if (value < MinTimeout) throw OnvifFault.TimeoutNotSupported($"Timeout {timeout} is shorter than {XmlConvert.ToString(MinTimeout)}.");
        return value < MaxTimeout ? value : MaxTimeout;
    }

    // Every profile of a PTZ camera carries its PTZ configuration.
    private void RequireProfile(string? token) => CameraMedia.CodecOfProfile(context, token);

    private static void CheckSpace(string? space, string supported)
    {
        if (!string.IsNullOrEmpty(space) && space != supported) throw OnvifFault.SpaceNotSupported(space);
    }

    private static bool InRange(float value, float min, float max) => value >= min && value <= max;
}
