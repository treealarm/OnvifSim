using System.Xml;
using OnvifSim.Onvif.Soap;
using OnvifSim.Onvif.Wsdl;
using OnvifSim.World;

namespace OnvifSim.Onvif.Services;

// Between the world's relay outputs and digital inputs and the ONVIF schema types describing them. The
// Device service and the DeviceIO service both answer about them, so the mapping lives here once.
public static class DeviceIoContract
{
    // ONVIF's RelayOutput describes a relay's settings only and leaves an xs:any for extensions, so the
    // live states travel in the simulator's own namespace beside them: the logical one (the coil
    // energised, which SetRelayOutputState sets) and the physical one (the contact, which follows from
    // it and IdleState). They are read here as well as from the Device/Trigger/Relay event.

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
        Any =
        [
            Extension("LogicalState", relay.Active ? "active" : "inactive"),
            Extension("PhysicalState", DeviceIo.ContactClosed(relay) ? "closed" : "open"),
        ],
    };

    // One element of the RelayOutput extension point, e.g. ons:PhysicalState with "closed".
    private static XmlElement Extension(string name, string value)
    {
        var element = new XmlDocument().CreateElement("ons", name, OnvifNamespaces.Extension);
        element.InnerText = value;
        return element;
    }

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
        // tt:FloatList is a space-separated list of seconds, not a list of xs:duration: the schema
        // says "supported delay time range or discrete values in seconds". Unlike RelayOutputSettings
        // .DelayTime, which really is a duration.
        DelayTimes = string.Join(' ', Delays.Select(delay => XmlConvert.ToString(delay.TotalSeconds))),
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
