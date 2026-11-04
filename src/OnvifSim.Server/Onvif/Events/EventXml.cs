using System.Globalization;
using System.Xml;
using OnvifSim.Onvif.Soap;
using OnvifSim.Onvif.Wsdl;

namespace OnvifSim.Onvif.Events;

// The pieces every ONVIF event message and topic tree is built from: a property's tt:Message with its
// PropertyOperation, the Source and Data items inside it, a topic expression, and the leaves of the tree
// GetEventProperties describes.
internal static class EventXml
{
    private const string TopicsSchema = "http://docs.oasis-open.org/wsn/t-1";
    private const string Tt = OnvifNamespaces.Schema;
    private const string Xs = "http://www.w3.org/2001/XMLSchema";

    // A notification on a topic, in the ConcreteSet dialect (tns1 is declared on the response).
    public static NotificationMessageHolderType Notification(string topic, XmlElement message) => new()
    {
        Topic = new TopicExpressionType
        {
            Dialect = MotionEvents.ConcreteSetDialect,
            Any = [new XmlDocument().CreateTextNode($"tns1:{topic}")],
        },
        Message = message,
    };

    // tt:Message: when it was raised and how it relates to the property it carries.
    public static XmlElement Message(DateTimeOffset time, string propertyOperation)
    {
        var document = new XmlDocument();
        var message = document.CreateElement("tt", "Message", Tt);
        message.SetAttribute("UtcTime", time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
        message.SetAttribute("PropertyOperation", propertyOperation);
        return message;
    }

    // Who raised it (Source) and what it says (Data), one tt:SimpleItem each.
    public static void Items(XmlElement message, string part, (string Name, string Value)[] items)
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

    // A top-level topic of GetEventProperties' tree, e.g. tns1:VideoSource, with the prefixes its
    // message descriptions use.
    public static XmlElement TopicRoot(XmlDocument document, string name)
    {
        var root = document.CreateElement("tns1", name, MotionEvents.TopicsNamespace);
        root.SetAttribute("xmlns:tt", Tt);
        root.SetAttribute("xmlns:xs", Xs);
        return root;
    }

    // One property leaf of the tree: a topic and the items its Source and Data carry.
    public static void Leaf(XmlDocument document, XmlElement parent, string name, (string Name, string Type)[] source, (string Name, string Type)[] data)
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
}
