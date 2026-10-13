using System.Reflection;
using System.ServiceModel;
using System.Xml;
using System.Xml.Linq;

namespace OnvifSim.Onvif.Soap;

// One ONVIF operation as the WCF contract interface generated from the ONVIF WSDL (Wsdl/generate.sh)
// describes it, which is the single source of truth for element names and namespaces. Every operation
// is generated as message contracts: a request and a response class with
// [MessageContract(WrapperName, WrapperNamespace)] whose fields are the body parts
// ([MessageBodyMember(Namespace, Order)], often [XmlElement]).
public sealed class OnvifOperation
{
    private OnvifOperation(string serviceNamespace, MethodInfo contractMethod)
    {
        var contract = contractMethod.GetCustomAttribute<OperationContractAttribute>()!;
        Name = contract.Name ?? StripAsync(contractMethod.Name);
        Namespace = serviceNamespace;

        RequestType = contractMethod.GetParameters() is [{ ParameterType: var requestType }] && IsMessageContract(requestType)
            ? requestType
            : throw new NotSupportedException($"{Name}: the request is not a single message contract; generate with --messageContract.");
        var returnType = contractMethod.ReturnType;
        ResponseType = returnType.IsGenericType && IsMessageContract(returnType.GetGenericArguments()[0])
            ? returnType.GetGenericArguments()[0]
            : throw new NotSupportedException($"{Name}: the response is not a message contract; generate with --messageContract.");
        RequestElement = MessageContractSerializer.Validate(RequestType);
        MessageContractSerializer.Validate(ResponseType);
    }

    public string Name { get; }
    public string Namespace { get; }
    // A handler method implementing the operation takes the request and returns the response.
    public Type RequestType { get; }
    public Type ResponseType { get; }
    // The element a request of this operation is in the SOAP body, by which requests are routed.
    public XName RequestElement { get; }

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

    public object ReadRequest(XElement body) => MessageContractSerializer.Get(RequestType).Read(body);

    public void WriteResponse(XmlWriter writer, object response) =>
        MessageContractSerializer.Get(ResponseType).Write(writer, response, OnvifNamespaces.Prefixes);

    private static bool IsMessageContract(Type type) => type.GetCustomAttribute<MessageContractAttribute>() != null;

    private static string StripAsync(string name) => name.EndsWith("Async", StringComparison.Ordinal) ? name[..^5] : name;
}
