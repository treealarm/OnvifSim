using System.Collections.Concurrent;
using System.Reflection;
using System.ServiceModel;
using System.Xml;
using System.Xml.Serialization;

namespace OnvifSim.Onvif.Soap;

// XmlSerializer constructors that take a root or overrides generate a new assembly on every call and
// never unload it, so every serializer is built once and kept.
internal static class XmlSerializerCache
{
    private static readonly ConcurrentDictionary<(Type, string, string), XmlSerializer> Serializers = new();

    // A serializer for a value written as the element {ns}name.
    public static XmlSerializer Get(Type type, string name, string ns) =>
        Serializers.GetOrAdd((type, name, ns), key => new XmlSerializer(key.Item1, new XmlRootAttribute(key.Item2) { Namespace = key.Item3 }));
}

// Serializes a WCF message contract class as its wrapper element: XmlSerializer reads the [XmlElement]
// attributes of the fields but not [MessageBodyMember(Namespace)], so the namespaces are added as
// overrides, keeping every other XML attribute of the field.
internal static class MessageContractSerializer
{
    private static readonly ConcurrentDictionary<Type, XmlSerializer> Serializers = new();

    public static XmlSerializer Get(Type contractType) => Serializers.GetOrAdd(contractType, Create);

    // Throws NotSupportedException for a contract this class cannot serialize. Cheap (reflection only),
    // unlike building the serializer, so it can run for every operation at startup.
    public static void Validate(Type contractType) => Overrides(contractType);

    private static XmlSerializer Create(Type contractType)
    {
        var (overrides, root) = Overrides(contractType);
        return new XmlSerializer(contractType, overrides, [], root, null);
    }

    private static (XmlAttributeOverrides Overrides, XmlRootAttribute Root) Overrides(Type contractType)
    {
        var contract = contractType.GetCustomAttribute<MessageContractAttribute>()
            ?? throw new ArgumentException($"{contractType} is not a message contract.");
        if (!contract.IsWrapped) throw new NotSupportedException($"Unwrapped message contract {contractType} is not supported.");

        var overrides = new XmlAttributeOverrides();
        var bodyMembers = contractType.GetFields()
            .Select(f => (Field: f, Body: f.GetCustomAttribute<MessageBodyMemberAttribute>()))
            .Where(m => m.Body != null);
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
        return (overrides, new XmlRootAttribute(contract.WrapperName ?? contractType.Name) { Namespace = contract.WrapperNamespace });
    }
}

// Conventional prefixes, so responses read like a camera's (tt:, tds:, trt:, tr2:) instead of d2p1:.
internal static class OnvifNamespaces
{
    public const string Schema = "http://www.onvif.org/ver10/schema";
    public const string Device = "http://www.onvif.org/ver10/device/wsdl";
    public const string Media = "http://www.onvif.org/ver10/media/wsdl";
    public const string Media2 = "http://www.onvif.org/ver20/media/wsdl";
    public const string Errors = "http://www.onvif.org/ver10/error";

    public static readonly XmlSerializerNamespaces Prefixes = new(
    [
        new XmlQualifiedName("tt", Schema),
        new XmlQualifiedName("tds", Device),
        new XmlQualifiedName("trt", Media),
        new XmlQualifiedName("tr2", Media2),
    ]);
}
