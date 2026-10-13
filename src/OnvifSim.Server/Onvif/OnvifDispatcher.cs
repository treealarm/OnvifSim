using System.Reflection;
using System.ServiceModel;
using System.Xml;
using System.Xml.Linq;
using OnvifSim.Onvif.Soap;
using OnvifSim.Security;

namespace OnvifSim.Onvif;

// Marks a handler class: its public methods implement operations of the WCF contract interface, each
// method named like the operation (GetProfiles for GetProfilesAsync), taking its request message and
// returning its response message. Path is where the service is served on the camera's port; Major/Minor
// go to GetServices.
[AttributeUsage(AttributeTargets.Class)]
public sealed class OnvifServiceAttribute(Type contract, string path, int major, int minor) : Attribute
{
    public Type Contract => contract;
    public string Path => path;
    public int Major => major;
    public int Minor => minor;
}

// An operation that answers without credentials, as the ONVIF PRE_AUTH access class and the lab camera
// do for GetSystemDateAndTime, GetCapabilities, GetServices and GetHostname.
[AttributeUsage(AttributeTargets.Method)]
public sealed class PreAuthAttribute : Attribute;

public sealed record OnvifServiceInfo(string Namespace, string Path, int Major, int Minor);

// Routes ONVIF requests to handler methods by the namespace and name of the body element, whatever path
// they were posted to: some clients send everything to device_service.
public sealed class OnvifDispatcher
{
    private sealed record Binding(OnvifOperation Operation, MethodInfo Method, bool PreAuth);

    private readonly Dictionary<XName, Binding> _bindings = [];
    private readonly HttpAuthenticator _authenticator;
    private readonly ILogger _logger;

    public OnvifDispatcher(IEnumerable<Type> handlerTypes, HttpAuthenticator authenticator, ILogger<OnvifDispatcher> logger)
    {
        _authenticator = authenticator;
        _logger = logger;
        var services = new List<OnvifServiceInfo>();
        foreach (var handlerType in handlerTypes)
        {
            var service = handlerType.GetCustomAttribute<OnvifServiceAttribute>()
                ?? throw new ArgumentException($"{handlerType} has no [OnvifService].");
            foreach (var method in handlerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var operation = OnvifOperation.Create(service.Contract, method.Name);
                CheckSignature(handlerType, method, operation);
                _bindings.Add(XName.Get(operation.Name, operation.Namespace),
                    new Binding(operation, method, method.GetCustomAttribute<PreAuthAttribute>() != null));
            }
            services.Add(new OnvifServiceInfo(
                service.Contract.GetCustomAttribute<ServiceContractAttribute>()!.Namespace!, service.Path, service.Major, service.Minor));
        }
        Services = services;
    }

    public IReadOnlyList<OnvifServiceInfo> Services { get; }

    public async Task HandleAsync(HttpContext http, OnvifRequestContext context)
    {
        string envelopeNamespace = SoapEnvelope.Soap12;
        try
        {
            var envelope = SoapEnvelope.Parse(await BufferAsync(http.Request.Body, http.RequestAborted));
            envelopeNamespace = envelope.EnvelopeNamespace;
            if (!_bindings.TryGetValue(envelope.Body.Name, out var binding))
                throw OnvifFault.ActionNotSupported(envelope.Body.Name.LocalName);

            if (!binding.PreAuth && !IsAuthorized(http, envelope, context))
            {
                await WriteUnauthorizedAsync(http, envelope, context);
                return;
            }

            var handler = Activator.CreateInstance(binding.Method.DeclaringType!, context)!;
            var request = binding.Operation.ReadRequest(envelope.Body);
            object response;
            try
            {
                response = binding.Method.Invoke(handler, [request])!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
            _logger.LogDebug("ONVIF {Camera}: {Operation}", context.Camera.Id, binding.Operation.Name);
            await WriteAsync(http, 200, envelopeNamespace, SoapEnvelope.Write(envelopeNamespace, w => binding.Operation.WriteResponse(w, response)));
        }
        catch (OnvifFault fault)
        {
            _logger.LogInformation("ONVIF {Camera}: fault {Subcodes}: {Reason}", context.Camera.Id, string.Join("/", fault.Subcodes), fault.Message);
            await WriteAsync(http, fault.IsSender ? 400 : 500, envelopeNamespace, SoapEnvelope.WriteFault(envelopeNamespace, fault));
        }
        catch (InvalidOperationException ex) when (ex.InnerException is XmlException or FormatException)
        {
            // XmlSerializer could not read an argument: a value of the wrong type or format.
            var fault = OnvifFault.InvalidArgValue(ex.InnerException.Message);
            await WriteAsync(http, 400, envelopeNamespace, SoapEnvelope.WriteFault(envelopeNamespace, fault));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A bug in a handler: the client still gets a SOAP fault rather than an empty 500.
            _logger.LogError(ex, "ONVIF {Camera}: request failed", context.Camera.Id);
            var fault = new OnvifFault(false, ["Action"], "Internal error in the simulator.");
            await WriteAsync(http, 500, envelopeNamespace, SoapEnvelope.WriteFault(envelopeNamespace, fault));
        }
    }

    // Runs the service's GetServiceCapabilities and returns its Capabilities element.
    public XmlElement? ServiceCapabilities(string serviceNamespace, OnvifRequestContext context)
    {
        if (!_bindings.TryGetValue(XName.Get("GetServiceCapabilities", serviceNamespace), out var binding)) return null;
        var request = Activator.CreateInstance(binding.Operation.RequestType);
        var response = binding.Method.Invoke(Activator.CreateInstance(binding.Method.DeclaringType!, context), [request])!;
        var document = new XmlDocument();
        using (var writer = document.CreateNavigator()!.AppendChild())
            binding.Operation.WriteResponse(writer, response);
        return document.DocumentElement!.FirstChild as XmlElement;
    }

    // A UsernameToken in the SOAP header or HTTP Digest/Basic, whichever the client sends.
    private bool IsAuthorized(HttpContext http, SoapEnvelope envelope, OnvifRequestContext context)
    {
        var users = context.Camera.Users;
        if (users.Count == 0) return true;
        var authorization = http.Request.Headers.Authorization.ToString();
        if (authorization.Length > 0 && _authenticator.IsAuthorized(http.Request.Method, authorization, users)) return true;
        return envelope.Security != null && UsernameToken.IsValid(envelope.Security, users, DateTime.UtcNow);
    }

    // Like the lab camera: a request with a bad UsernameToken gets a NotAuthorized fault, a request
    // with no credentials (or bad HTTP ones) an HTTP 401 challenge, which WCF clients answer with Digest.
    private async Task WriteUnauthorizedAsync(HttpContext http, SoapEnvelope envelope, OnvifRequestContext context)
    {
        _logger.LogInformation("ONVIF {Camera}: {Operation} refused, no valid credentials", context.Camera.Id, envelope.Body.Name.LocalName);
        var fault = SoapEnvelope.WriteFault(envelope.EnvelopeNamespace, OnvifFault.NotAuthorized());
        if (envelope.Security != null && http.Request.Headers.Authorization.Count == 0)
        {
            await WriteAsync(http, 400, envelope.EnvelopeNamespace, fault);
            return;
        }
        foreach (var challenge in _authenticator.Challenges()) http.Response.Headers.Append("WWW-Authenticate", challenge);
        await WriteAsync(http, 401, envelope.EnvelopeNamespace, fault);
    }

    private static async Task WriteAsync(HttpContext http, int status, string envelopeNamespace, byte[] body)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = SoapEnvelope.ContentType(envelopeNamespace);
        http.Response.ContentLength = body.Length;
        await http.Response.Body.WriteAsync(body, http.RequestAborted);
    }

    // XmlReader reads synchronously; Kestrel forbids synchronous reads of the request body.
    private static async Task<MemoryStream> BufferAsync(Stream body, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        await body.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        return buffer;
    }

    private static void CheckSignature(Type handlerType, MethodInfo method, OnvifOperation operation)
    {
        if (method.GetParameters() is not [{ ParameterType: var parameter }] || parameter != operation.RequestType || method.ReturnType != operation.ResponseType)
            throw new InvalidOperationException(
                $"{handlerType.Name}.{method.Name} must be ({operation.RequestType.Name}) -> {operation.ResponseType.Name} to implement {operation.Name}.");
    }
}
