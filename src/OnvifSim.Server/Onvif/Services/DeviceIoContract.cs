using System.Xml;
using OnvifSim.Onvif.Soap;
using OnvifSim.Onvif.Wsdl;
using OnvifSim.World;

namespace OnvifSim.Onvif.Services;

// Between the world's relay outputs and digital inputs and the ONVIF schema types describing them. The
// Device service and the DeviceIO service both answer about them, so the mapping lives here once.
public static class DeviceIoContract
{
    // What GetRelayOutputOptions offers for every relay: both modes, and delays a monostable one might
    // be set to.
    private static readonly TimeSpan[] Delays =
        [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
         TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    public static RelayOutput RelayOutputOf(RelayOutputModel relay) => new()
    {
        token = relay.Token,
        Properties = new RelayOutputSettings
        {
            Mode = relay.Mode == RelayModeModel.Monostable ? RelayMode.Monostable : RelayMode.Bistable,
            DelayTime = XmlConvert.ToString(TimeSpan.FromSeconds(relay.DelaySeconds)),
            IdleState = relay.IdleState == IdleStateModel.Closed ? RelayIdleState.closed : RelayIdleState.open,
        },
    };

    public static DigitalInput DigitalInputOf(DigitalInputModel input) => new()
    {
        token = input.Token,
        IdleState = input.IdleState == IdleStateModel.Closed ? DigitalIdleState.closed : DigitalIdleState.open,
        IdleStateSpecified = true,
    };

    public static RelayOutputOptions RelayOptionsOf(RelayOutputModel relay) => new()
    {
        token = relay.Token,
        Mode = [RelayMode.Bistable, RelayMode.Monostable],
        DelayTimes = string.Join(' ', Delays.Select(delay => XmlConvert.ToString(delay))),
        Discrete = false,
        DiscreteSpecified = true,
    };

    public static RelayModeModel ModeOf(RelayMode mode) =>
        mode == RelayMode.Monostable ? RelayModeModel.Monostable : RelayModeModel.Bistable;

    public static IdleStateModel IdleStateOf(RelayIdleState idle) =>
        idle == RelayIdleState.closed ? IdleStateModel.Closed : IdleStateModel.Open;

    public static IdleStateModel IdleStateOf(DigitalIdleState idle) =>
        idle == DigitalIdleState.closed ? IdleStateModel.Closed : IdleStateModel.Open;

    // A delay as xs:duration, in seconds. Null when the request leaves it out, which keeps the stored one.
    public static float? DelaySecondsOf(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration)) return null;
        try
        {
            return (float)XmlConvert.ToTimeSpan(duration.Trim()).TotalSeconds;
        }
        catch (FormatException)
        {
            throw OnvifFault.InvalidArgValue($"DelayTime '{duration}' is not a duration.");
        }
    }
}
