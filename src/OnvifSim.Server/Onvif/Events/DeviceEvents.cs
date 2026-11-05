using System.Xml;
using OnvifSim.Onvif.Wsdl;

namespace OnvifSim.Onvif.Events;

// The events a camera raises about its relay outputs and digital inputs. ONVIF has no getter for their
// live state, so it travels as property events: a subscriber gets the current state of each device as
// "Initialized", and every change as "Changed". A digital input's state is the state of the relay its
// wire runs from, so a wire across two cameras makes the change arrive on the input's camera.
//
// A relay has two states and both are reported: the logical one (LogicalState, active/inactive — the
// coil, which SetRelayOutputState sets) and the physical one (PhysicalState, closed/open — the contact,
// which follows from it and the relay's idle state). An input has only the logical one.
public static class DeviceEvents
{
    public const string RelayTopic = "Device/Trigger/Relay";
    public const string DigitalInputTopic = "Device/Trigger/DigitalInput";

    public static readonly string[] Topics = [RelayTopic, DigitalInputTopic];

    // One device of the camera, on its own topic, with the state as ONVIF writes it for that kind. A
    // relay also says how its contact stands; an input has no contact of its own.
    public static NotificationMessageHolderType Notification(string topic, string token, bool state, bool? contactClosed, string propertyOperation, DateTimeOffset time)
    {
        bool relay = topic == RelayTopic;
        var message = EventXml.Message(time, propertyOperation);
        EventXml.Items(message, "Source", [(relay ? "RelayToken" : "InputToken", token)]);
        var data = new List<(string Name, string Value)> { ("LogicalState", LogicalState(topic, state)) };
        if (contactClosed is { } closed) data.Add(("PhysicalState", closed ? "closed" : "open"));
        EventXml.Items(message, "Data", [.. data]);
        return EventXml.Notification(topic, message);
    }

    // A relay's logical state is active/inactive, a digital input's true/false (ONVIF Core, the
    // Device/Trigger topics).
    public static string LogicalState(string topic, bool state) =>
        topic == RelayTopic ? state ? "active" : "inactive" : state ? "true" : "false";

    // The tree of GetEventProperties: Device/Trigger with a leaf for each kind.
    public static TopicSetType TopicSet()
    {
        var document = new XmlDocument();
        var device = EventXml.TopicRoot(document, "Device");
        var trigger = (XmlElement)device.AppendChild(document.CreateElement("Trigger"))!;
        EventXml.Leaf(document, trigger, "Relay",
            [("RelayToken", "tt:ReferenceToken")], [("LogicalState", "xs:string"), ("PhysicalState", "xs:string")]);
        EventXml.Leaf(document, trigger, "DigitalInput",
            [("InputToken", "tt:ReferenceToken")], [("LogicalState", "xs:string")]);
        return new TopicSetType { Any = [device] };
    }
}
