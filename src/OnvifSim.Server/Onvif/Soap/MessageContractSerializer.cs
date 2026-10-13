using System.Collections.Concurrent;
using System.Reflection;
using System.ServiceModel;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace OnvifSim.Onvif.Soap;

// Serializes a WCF message contract class as the element it puts in the SOAP body. A wrapped contract
// is its wrapper element, whose children are the fields: XmlSerializer reads the [XmlElement] attributes
// of the fields but not [MessageBodyMember(Namespace)], so the namespaces are added as overrides, keeping
// every other XML attribute of the field. An unwrapped contract with a single body member (the WS-N
// operations, e.g. wsnt:Renew) is that member's element. A serializer built with overrides or a root is a
// new assembly on every construction that is never unloaded, so each one is built once and kept.
internal sealed class MessageContractSerializer
{
    private static readonly ConcurrentDictionary<Type, MessageContractSerializer> Serializers = new();

    private readonly Type _contractType;
    private readonly XmlSerializer _serializer;
    // The field that is the body element, for an unwrapped contract.
    private readonly FieldInfo? _part;

    private MessageContractSerializer(Type contractType)
    {
        _contractType = contractType;
        var shape = Describe(contractType);
        (BodyElement, _part) = (shape.BodyElement, shape.Part);
        var root = new XmlRootAttribute(BodyElement.LocalName) { Namespace = BodyElement.NamespaceName };
        _serializer = shape.Part is { } part
            ? new XmlSerializer(part.FieldType, null, [], root, null)
            : new XmlSerializer(contractType, shape.Overrides, [], root, null);
    }

    // The name of the element the contract is in the body.
    public XName BodyElement { get; }

    public static MessageContractSerializer Get(Type contractType) => Serializers.GetOrAdd(contractType, t => new MessageContractSerializer(t));

    // Throws NotSupportedException for a contract this class cannot serialize. Cheap (reflection only),
    // unlike building the serializer, so it can run for every operation at startup.
    public static XName Validate(Type contractType) => Describe(contractType).BodyElement;

    public object Read(XElement body)
    {
        var value = _serializer.Deserialize(body.CreateReader())!;
        if (_part == null) return value;
        var message = Activator.CreateInstance(_contractType)!;
        _part.SetValue(message, value);
        return message;
    }

    public void Write(XmlWriter writer, object message, XmlSerializerNamespaces prefixes) =>
        _serializer.Serialize(writer, _part == null ? message : _part.GetValue(message), prefixes);

    private sealed record Shape(XName BodyElement, XmlAttributeOverrides? Overrides, FieldInfo? Part);

    private static Shape Describe(Type contractType)
    {
        var contract = contractType.GetCustomAttribute<MessageContractAttribute>()
            ?? throw new ArgumentException($"{contractType} is not a message contract.");
        var bodyMembers = contractType.GetFields()
            .Select(f => (Field: f, Body: f.GetCustomAttribute<MessageBodyMemberAttribute>()))
            .Where(m => m.Body != null)
            .ToList();

        if (!contract.IsWrapped)
        {
            if (bodyMembers is not [var (part, partBody)])
                throw new NotSupportedException($"Unwrapped message contract {contractType} has {bodyMembers.Count} body members, not one.");
            return new Shape(XName.Get(partBody!.Name ?? part.Name, partBody.Namespace ?? ""), null, part);
        }

        var overrides = new XmlAttributeOverrides();
        foreach (var (field, body) in bodyMembers)
        {
            var attributes = new XmlAttributes(field);
            var ns = body!.Namespace ?? contract.WrapperNamespace;
            if (attributes.XmlArray != null)
                throw new NotSupportedException($"{contractType.Name}.{field.Name}: [XmlArray] body members are not supported.");
            if (attributes.XmlElements.Count == 0)
                attributes.XmlElements.Add(new XmlElementAttribute(body.Name ?? field.Name));
            foreach (XmlElementAttribute element in attributes.XmlElements)
            {
                element.Namespace = ns;
                element.Form = System.Xml.Schema.XmlSchemaForm.Qualified;
            }
            overrides.Add(contractType, field.Name, attributes);
        }
        return new Shape(XName.Get(contract.WrapperName ?? contractType.Name, contract.WrapperNamespace ?? ""), overrides, null);
    }
}

// Conventional prefixes, so responses read like a camera's (tt:, tds:, trt:, tr2:, ...) instead of d2p1:.
internal static class OnvifNamespaces
{
    public const string Schema = "http://www.onvif.org/ver10/schema";
    public const string Device = "http://www.onvif.org/ver10/device/wsdl";
    public const string Media = "http://www.onvif.org/ver10/media/wsdl";
    public const string Media2 = "http://www.onvif.org/ver20/media/wsdl";
    public const string Ptz = "http://www.onvif.org/ver20/ptz/wsdl";
    public const string Imaging = "http://www.onvif.org/ver20/imaging/wsdl";
    public const string Errors = "http://www.onvif.org/ver10/error";
    public const string Events = "http://www.onvif.org/ver10/events/wsdl";
    public const string Notification = "http://docs.oasis-open.org/wsn/b-2";
    public const string Addressing = "http://www.w3.org/2005/08/addressing";
    public const string Topics = "http://www.onvif.org/ver10/topics";

    public static readonly XmlSerializerNamespaces Prefixes = new(
    [
        new XmlQualifiedName("tt", Schema),
        new XmlQualifiedName("tds", Device),
        new XmlQualifiedName("trt", Media),
        new XmlQualifiedName("tr2", Media2),
        new XmlQualifiedName("tptz", Ptz),
        new XmlQualifiedName("timg", Imaging),
        new XmlQualifiedName("tev", Events),
        new XmlQualifiedName("wsnt", Notification),
        new XmlQualifiedName("wsa", Addressing),
        // Topic expressions are text: the tns1 they use must be declared around them.
        new XmlQualifiedName("tns1", Topics),
    ]);
}
