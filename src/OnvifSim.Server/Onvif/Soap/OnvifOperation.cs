using System.Reflection;
using System.ServiceModel;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace OnvifSim.Onvif.Soap;

// One ONVIF operation as the WCF contract interface generated in OnvifLib describes it, which is the
// single source of truth for element names and namespaces. svcutil emits two shapes and both occur:
//   - message contracts: a request/response class with [MessageContract(WrapperName, WrapperNamespace)]
//     whose fields are the body parts ([MessageBodyMember(Namespace, Order)], often [XmlElement]);
//   - unwrapped: plain parameters and a return value named by [return: MessageParameter(Name)], the
//     wrapper being the operation name (+ "Response") in the service namespace.
// XmlSerializer knows neither shape, so this class turns them into XmlSerializer calls.
public sealed class OnvifOperation
{
    private readonly Func<XElement, object?[]> _readArguments;
    private readonly Action<XmlWriter, object?> _writeResult;

    private OnvifOperation(string serviceNamespace, MethodInfo contractMethod)
    {
        var contract = contractMethod.GetCustomAttribute<OperationContractAttribute>()!;
        Name = contract.Name ?? StripAsync(contractMethod.Name);
        Namespace = serviceNamespace;

        var parameters = contractMethod.GetParameters();
        if (parameters is [{ ParameterType: var requestType }] && requestType.GetCustomAttribute<MessageContractAttribute>() != null)
        {
            ParameterTypes = [requestType];
            MessageContractSerializer.Validate(requestType);
            _readArguments = body => [MessageContractSerializer.Get(requestType).Deserialize(body.CreateReader())];
        }
        else
        {
            ParameterTypes = parameters.Select(p => p.ParameterType).ToArray();
            var parts = parameters.Select(p => MessagePart.FromParameter(p, p.ParameterType, serviceNamespace)).ToArray();
            _readArguments = body => parts.Select(part => part.Read(body)).ToArray();
        }

        var returnType = contractMethod.ReturnType;
        ResultType = returnType.IsGenericType ? returnType.GetGenericArguments()[0] : typeof(void);
        if (ResultType.GetCustomAttribute<MessageContractAttribute>() != null)
        {
            var resultType = ResultType;
            MessageContractSerializer.Validate(resultType);
            _writeResult = (writer, result) => MessageContractSerializer.Get(resultType).Serialize(writer, result!, OnvifNamespaces.Prefixes);
        }
        else
        {
            var responseName = Name + "Response";
            var part = ResultType == typeof(void) ? null : MessagePart.FromParameter(contractMethod.ReturnParameter, ResultType, serviceNamespace);
            _writeResult = (writer, result) =>
            {
                writer.WriteStartElement(responseName, serviceNamespace);
                part?.Write(writer, result);
                writer.WriteEndElement();
            };
        }
    }

    public string Name { get; }
    public string Namespace { get; }
    // What a handler method must take and return to implement the operation (void: no result).
    public Type[] ParameterTypes { get; }
    public Type ResultType { get; }

    // The operation of a WCF service contract interface implemented by the method of that name
    // (e.g. "GetProfiles" for GetProfilesAsync). Built only for operations the simulator implements,
    // so a shape this class cannot handle fails at startup, not on a request.
    public static OnvifOperation Create(Type contractInterface, string name)
    {
        var serviceNamespace = contractInterface.GetCustomAttribute<ServiceContractAttribute>()?.Namespace
            ?? throw new ArgumentException($"{contractInterface} is not a WCF service contract.");
        var method = contractInterface.GetMethod(name + "Async")
            ?? throw new ArgumentException($"{contractInterface.Name} has no operation {name}.");
        return new OnvifOperation(serviceNamespace, method);
    }

    public object?[] ReadArguments(XElement body) => _readArguments(body);

    public void WriteResult(XmlWriter writer, object? result) => _writeResult(writer, result);

    private static string StripAsync(string name) => name.EndsWith("Async", StringComparison.Ordinal) ? name[..^5] : name;
}

// One body element of an unwrapped operation: a parameter or the return value.
internal sealed class MessagePart
{
    private readonly string _name;
    private readonly string _namespace;
    private readonly Type _type;
    // Arrays marked [XmlElement] are written as repeated elements, one per item.
    private readonly Type? _repeatedItemType;

    private MessagePart(string name, string ns, Type type, XmlAttributes attributes)
    {
        _name = name;
        _namespace = ns;
        _type = type;
        if (type.IsArray && type != typeof(byte[]))
        {
            if (attributes.XmlElements.Count == 0)
                throw new NotSupportedException($"Array part '{name}' without [XmlElement] is not supported.");
            _repeatedItemType = type.GetElementType();
        }
    }

    // type is the parameter's own type, or T for a return value of Task<T>.
    public static MessagePart FromParameter(ParameterInfo parameter, Type type, string serviceNamespace)
    {
        var attributes = new XmlAttributes(parameter);
        string name = parameter.GetCustomAttribute<MessageParameterAttribute>()?.Name
            ?? (attributes.XmlElements.Count > 0 && !string.IsNullOrEmpty(attributes.XmlElements[0]!.ElementName) ? attributes.XmlElements[0]!.ElementName : null)
            ?? parameter.Name
            ?? throw new NotSupportedException("A return value needs [MessageParameter(Name)].");
        return new MessagePart(name, serviceNamespace, type, attributes);
    }

    public object? Read(XElement body)
    {
        var elements = body.Elements(XName.Get(_name, _namespace)).ToList();
        if (_repeatedItemType != null)
        {
            var serializer = XmlSerializerCache.Get(_repeatedItemType, _name, _namespace);
            var items = Array.CreateInstance(_repeatedItemType, elements.Count);
            for (int i = 0; i < elements.Count; i++) items.SetValue(serializer.Deserialize(elements[i].CreateReader()), i);
            return items;
        }
        if (elements.Count == 0) return _type.IsValueType ? Activator.CreateInstance(_type) : null;
        return XmlSerializerCache.Get(_type, _name, _namespace).Deserialize(elements[0].CreateReader());
    }

    public void Write(XmlWriter writer, object? value)
    {
        if (value == null) return;
        if (_repeatedItemType != null)
        {
            var serializer = XmlSerializerCache.Get(_repeatedItemType, _name, _namespace);
            foreach (var item in (Array)value) serializer.Serialize(writer, item, OnvifNamespaces.Prefixes);
            return;
        }
        XmlSerializerCache.Get(_type, _name, _namespace).Serialize(writer, value, OnvifNamespaces.Prefixes);
    }
}
