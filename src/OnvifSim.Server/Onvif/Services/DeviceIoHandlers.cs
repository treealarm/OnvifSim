using OnvifSim.Onvif.Soap;
using OnvifSim.Onvif.Wsdl;
using OnvifSim.Onvif.Wsdl.DeviceIo;
using OnvifSim.World;

namespace OnvifSim.Onvif.Services;

// The DeviceIO service (ver10): the camera's digital inputs and what can be configured on them, and the
// options its relay outputs offer. The relay outputs themselves are described by the Device service's
// messages — the ONVIF DeviceIO WSDL reuses them, so GetRelayOutputs and SetRelayOutputState arrive in
// the Device namespace and are answered by DeviceHandlers; a second binding for them would collide.
[OnvifService(typeof(DeviceIOPort), ServicePath, 21, 12)]
public sealed class DeviceIoHandlers(OnvifRequestContext context)
{
    public const string ServicePath = "deviceio_service";

    private CameraModel Camera => context.Camera;

    public GetServiceCapabilitiesResponse GetServiceCapabilities(GetServiceCapabilitiesRequest request) => new()
    {
        Capabilities = new Capabilities1
        {
            RelayOutputs = Camera.RelayOutputs.Count,
            DigitalInputs = Camera.DigitalInputs.Count,
            DigitalInputOptions = true,
        },
    };

    // The camera's digital inputs: the token addresses the input, and IdleState is the state its contact
    // rests in. Whether it is on now is not ONVIF's to tell here; that travels as a
    // Device/Trigger/DigitalInput property event.
    public GetDigitalInputsResponse GetDigitalInputs(GetDigitalInputsRequest request) => new()
    {
        DigitalInputs = context.CurrentCamera.DigitalInputs.Select(DeviceIoContract.DigitalInputOf).ToArray(),
    };

    public GetDigitalInputConfigurationOptionsResponse GetDigitalInputConfigurationOptions(GetDigitalInputConfigurationOptionsRequest request)
    {
        RequireInput(context.CurrentCamera, request.Token);
        return new GetDigitalInputConfigurationOptionsResponse(new DigitalInputConfigurationOptions
        {
            IdleState = [DigitalIdleState.open, DigitalIdleState.closed],
        });
    }

    public SetDigitalInputConfigurationsResponse SetDigitalInputConfigurations(SetDigitalInputConfigurationsRequest request)
    {
        context.Store.Update(world =>
        {
            foreach (var configuration in request.DigitalInputs ?? [])
            {
                var input = DeviceIo.FindInput(world, Camera.Id, configuration.token)
                    ?? throw OnvifFault.InvalidArgValue($"The camera has no digital input '{configuration.token}'.");
                input.IdleState = DeviceIoContract.IdleStateOf(configuration.IdleState);
            }
        });
        return new SetDigitalInputConfigurationsResponse();
    }

    // Both modes and the delays a monostable relay may be set to, for every relay or for the one named.
    public GetRelayOutputOptionsResponse GetRelayOutputOptions(GetRelayOutputOptionsRequest request)
    {
        var relays = context.CurrentCamera.RelayOutputs;
        if (!string.IsNullOrWhiteSpace(request.RelayOutputToken))
        {
            var relay = relays.FirstOrDefault(r => DeviceIo.Same(r.Token, request.RelayOutputToken))
                ?? throw OnvifFault.InvalidArgValue($"The camera has no relay output '{request.RelayOutputToken}'.");
            relays = [relay];
        }
        return new GetRelayOutputOptionsResponse { RelayOutputOptions = relays.Select(DeviceIoContract.RelayOptionsOf).ToArray() };
    }

    private static void RequireInput(CameraModel camera, string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || camera.DigitalInputs.All(input => !DeviceIo.Same(input.Token, token)))
            throw OnvifFault.InvalidArgValue($"The camera has no digital input '{token}'.");
    }
}
