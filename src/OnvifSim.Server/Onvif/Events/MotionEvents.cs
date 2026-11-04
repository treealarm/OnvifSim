using System.Xml;
using OnvifSim.Onvif.Services;
using OnvifSim.Onvif.Soap;
using OnvifSim.Onvif.Wsdl;

namespace OnvifSim.Onvif.Events;

// The events a simulated camera raises: motion, while its analytics sees someone or a car. Like most
// cameras it says so on two topics, the motion alarm of the video source and the cell motion detector's
// rule; both are properties, so a subscriber first gets their current state ("Initialized") and then every
// change ("Changed").
public static class MotionEvents
{
    public const string TopicsNamespace = OnvifNamespaces.Topics;
    public const string ConcreteSetDialect = "http://www.onvif.org/ver10/tev/topicExpression/ConcreteSet";
    public const string MotionAlarmTopic = "VideoSource/MotionAlarm";
    public const string CellMotionTopic = "RuleEngine/CellMotionDetector/Motion";
    public const string MotionRule = "MotionDetection";

    public static readonly string[] Topics = [MotionAlarmTopic, CellMotionTopic];

    // The notifications that say whether there is motion, one per topic the subscriber wants.
    public static NotificationMessageHolderType[] Notifications(bool motion, string propertyOperation, DateTimeOffset time, Func<string, bool> wants) =>
        Topics.Where(wants).Select(topic => EventXml.Notification(topic, Message(topic, motion, propertyOperation, time))).ToArray();

    // The topic tree of GetEventProperties, with the message each leaf carries (ONVIF Core 9.5.6).
    public static TopicSetType TopicSet()
    {
        var document = new XmlDocument();
        var videoSource = EventXml.TopicRoot(document, "VideoSource");
        EventXml.Leaf(document, videoSource, "MotionAlarm",
            [("Source", "tt:ReferenceToken")], [("State", "xs:boolean")]);
        var ruleEngine = EventXml.TopicRoot(document, "RuleEngine");
        var cellMotion = (XmlElement)ruleEngine.AppendChild(document.CreateElement("CellMotionDetector"))!;
        EventXml.Leaf(document, cellMotion, "Motion",
            [("VideoSourceConfigurationToken", "tt:ReferenceToken"), ("VideoAnalyticsConfigurationToken", "tt:ReferenceToken"), ("Rule", "xs:string")],
            [("IsMotion", "xs:boolean")]);
        return new TopicSetType { Any = [videoSource, ruleEngine] };
    }

    // tt:Message: who raised it (Source) and what it says (Data).
    private static XmlElement Message(string topic, bool motion, string propertyOperation, DateTimeOffset time)
    {
        var message = EventXml.Message(time, propertyOperation);
        var (source, data) = topic == MotionAlarmTopic
            ? (new[] { ("Source", CameraMedia.VideoSourceToken) }, ("State", motion))
            : (new[]
            {
                ("VideoSourceConfigurationToken", CameraMedia.VideoSourceConfigurationToken),
                ("VideoAnalyticsConfigurationToken", CameraMedia.AnalyticsConfigurationToken),
                ("Rule", MotionRule),
            }, ("IsMotion", motion));
        EventXml.Items(message, "Source", source);
        EventXml.Items(message, "Data", [(data.Item1, motion ? "true" : "false")]);
        return message;
    }
}
