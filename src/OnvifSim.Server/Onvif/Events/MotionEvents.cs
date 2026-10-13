using System.Xml;
using OnvifSim.Onvif.Services;
using OnvifSim.Onvif.Soap;
using OnvifSim.Onvif.Wsdl;

namespace OnvifSim.Onvif.Events;

// The events a simulated camera raises: motion, while its analytics sees someone. Like most cameras it
// says so on two topics, the motion alarm of the video source and the cell motion detector's rule;
// both are properties, so a subscriber first gets their current state ("Initialized") and then every
// change ("Changed").
public static class MotionEvents
{
    public const string TopicsNamespace = OnvifNamespaces.Topics;
    public const string ConcreteSetDialect = "http://www.onvif.org/ver10/tev/topicExpression/ConcreteSet";
    public const string MotionAlarmTopic = "VideoSource/MotionAlarm";
    public const string CellMotionTopic = "RuleEngine/CellMotionDetector/Motion";
    public const string MotionRule = "MotionDetection";

    private const string TopicsSchema = "http://docs.oasis-open.org/wsn/t-1";
    private const string Tt = OnvifNamespaces.Schema;
    private const string Xs = "http://www.w3.org/2001/XMLSchema";

    public static readonly string[] Topics = [MotionAlarmTopic, CellMotionTopic];

    // The notifications that say whether there is motion, one per topic the subscriber wants.
    public static NotificationMessageHolderType[] Notifications(bool motion, string propertyOperation, DateTimeOffset time, Func<string, bool> wants) =>
        Topics.Where(wants).Select(topic => new NotificationMessageHolderType
        {
            // The expression is the element's text; tns1 is declared on the response (OnvifNamespaces).
            Topic = new TopicExpressionType { Dialect = ConcreteSetDialect, Any = [new XmlDocument().CreateTextNode($"tns1:{topic}")] },
            Message = Message(topic, motion, propertyOperation, time),
        }).ToArray();

    // The topic tree of GetEventProperties, with the message each leaf carries (ONVIF Core 9.5.6).
    public static TopicSetType TopicSet()
    {
        var document = new XmlDocument();
        var videoSource = TopicRoot(document, "VideoSource");
        Leaf(document, videoSource, "MotionAlarm",
            [("Source", "tt:ReferenceToken")], [("State", "xs:boolean")]);
        var ruleEngine = TopicRoot(document, "RuleEngine");
        var cellMotion = (XmlElement)ruleEngine.AppendChild(document.CreateElement("CellMotionDetector"))!;
        Leaf(document, cellMotion, "Motion",
            [("VideoSourceConfigurationToken", "tt:ReferenceToken"), ("VideoAnalyticsConfigurationToken", "tt:ReferenceToken"), ("Rule", "xs:string")],
            [("IsMotion", "xs:boolean")]);
        return new TopicSetType { Any = [videoSource, ruleEngine] };
    }

    private static XmlElement TopicRoot(XmlDocument document, string name)
    {
        var root = document.CreateElement("tns1", name, TopicsNamespace);
        root.SetAttribute("xmlns:tt", Tt);
        root.SetAttribute("xmlns:xs", Xs);
        return root;
    }

    private static void Leaf(XmlDocument document, XmlElement parent, string name, (string Name, string Type)[] source, (string Name, string Type)[] data)
    {
        var leaf = (XmlElement)parent.AppendChild(document.CreateElement(name))!;
        var isTopic = document.CreateAttribute("wstop", "topic", TopicsSchema);
        isTopic.Value = "true";
        leaf.Attributes.Append(isTopic);
        var description = (XmlElement)leaf.AppendChild(document.CreateElement("tt", "MessageDescription", Tt))!;
        description.SetAttribute("IsProperty", "true");
        foreach (var (part, items) in new[] { ("Source", source), ("Data", data) })
        {
            var element = description.AppendChild(document.CreateElement("tt", part, Tt))!;
            foreach (var (itemName, type) in items)
            {
                var item = (XmlElement)element.AppendChild(document.CreateElement("tt", "SimpleItemDescription", Tt))!;
                item.SetAttribute("Name", itemName);
                item.SetAttribute("Type", type);
            }
        }
    }

    // tt:Message: who raised it (Source) and what it says (Data).
    private static XmlElement Message(string topic, bool motion, string propertyOperation, DateTimeOffset time)
    {
        var document = new XmlDocument();
        var message = document.CreateElement("tt", "Message", Tt);
        message.SetAttribute("UtcTime", time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture));
        message.SetAttribute("PropertyOperation", propertyOperation);
        var (source, data) = topic == MotionAlarmTopic
            ? (new[] { ("Source", CameraMedia.VideoSourceToken) }, ("State", motion))
            : (new[]
            {
                ("VideoSourceConfigurationToken", CameraMedia.VideoSourceConfigurationToken),
                ("VideoAnalyticsConfigurationToken", CameraMedia.AnalyticsConfigurationToken),
                ("Rule", MotionRule),
            }, ("IsMotion", motion));
        Items(message, "Source", source);
        Items(message, "Data", [(data.Item1, motion ? "true" : "false")]);
        return message;
    }

    private static void Items(XmlElement message, string part, (string Name, string Value)[] items)
    {
        var document = message.OwnerDocument;
        var element = message.AppendChild(document.CreateElement("tt", part, Tt))!;
        foreach (var (name, value) in items)
        {
            var item = (XmlElement)element.AppendChild(document.CreateElement("tt", "SimpleItem", Tt))!;
            item.SetAttribute("Name", name);
            item.SetAttribute("Value", value);
        }
    }
}
