using System.Xml;
using OnvifSim.Onvif.Events;
using OnvifSim.Onvif.Soap;
using OnvifSim.Onvif.Wsdl;
using OnvifSim.Onvif.Wsdl.Events;

namespace OnvifSim.Onvif.Services;

// Events (ver10): motion events through pull points, the way ONVIF Profile S clients read them. A client
// creates a subscription here and then pulls from, renews and ends it at the address it was given
// (PullPointHandlers, SubscriptionManagerHandlers). Notifications pushed to a client (Subscribe) are not
// offered.
[OnvifService(typeof(EventPortType), "event_service", 21, 12)]
public sealed class EventHandlers(OnvifRequestContext context)
{
    public const string SubscriptionPath = "event_service/subscription/";

    private static readonly string[] TopicExpressionDialects =
        [MotionEvents.ConcreteSetDialect, "http://docs.oasis-open.org/wsn/t-1/TopicExpression/Concrete"];

    public GetServiceCapabilitiesResponse GetServiceCapabilities(GetServiceCapabilitiesRequest request) => new()
    {
        Capabilities = new Capabilities5
        {
            WSSubscriptionPolicySupport = false, WSSubscriptionPolicySupportSpecified = true,
            WSPausableSubscriptionManagerInterfaceSupport = false, WSPausableSubscriptionManagerInterfaceSupportSpecified = true,
            MaxNotificationProducers = 0, MaxNotificationProducersSpecified = true,
            MaxPullPoints = PullPoints.MaxPerCamera, MaxPullPointsSpecified = true,
            PersistentNotificationStorage = false, PersistentNotificationStorageSpecified = true,
        },
    };

    public GetEventPropertiesResponse GetEventProperties(GetEventPropertiesRequest request) => new()
    {
        TopicNamespaceLocation = ["http://www.onvif.org/onvif/ver10/topics/topicns.xml"],
        FixedTopicSet = true,
        TopicSet = MotionEvents.TopicSet(),
        TopicExpressionDialect = TopicExpressionDialects,
        MessageContentFilterDialect = ["http://www.onvif.org/ver10/tev/messageContentFilter/ItemFilter"],
        MessageContentSchemaLocation = ["http://www.onvif.org/onvif/ver10/schema/onvif.xsd"],
    };

    public CreatePullPointSubscriptionResponse CreatePullPointSubscription(CreatePullPointSubscriptionRequest request)
    {
        var pullPoints = context.PullPoints;
        var subscription = pullPoints.Create(context.Camera.Id, request.Filter, pullPoints.LifetimeOf(request.InitialTerminationTime));
        return new CreatePullPointSubscriptionResponse
        {
            SubscriptionReference = new EndpointReferenceType
            {
                Address = new AttributedURIType { Value = context.XAddr(SubscriptionPath + subscription.Id) },
            },
            CurrentTime = pullPoints.Time.GetUtcNow().UtcDateTime,
            TerminationTime = subscription.TerminationTime.UtcDateTime,
        };
    }

    // The subscription a request was posted to, by the id at the end of its address.
    public static PullPoint SubscriptionOf(OnvifRequestContext context)
    {
        int at = context.Path.IndexOf(SubscriptionPath, StringComparison.OrdinalIgnoreCase);
        return context.PullPoints.Find(context.Camera.Id, at < 0 ? null : context.Path[(at + SubscriptionPath.Length)..]);
    }
}

// What a client does at its pull point's address: pull the notifications, ask for the current state again.
[OnvifService(typeof(PullPointSubscription), "event_service", 21, 12, Listed = false)]
public sealed class PullPointHandlers(OnvifRequestContext context)
{
    // The longest a PullMessages waits, whatever the client asks for.
    private static readonly TimeSpan MaxPullTimeout = TimeSpan.FromMinutes(1);

    // Waits for notifications; pulling keeps the subscription alive for another lifetime, as cameras do.
    public async Task<PullMessagesResponse> PullMessages(PullMessagesRequest request)
    {
        var subscription = EventHandlers.SubscriptionOf(context);
        TimeSpan timeout;
        try
        {
            timeout = XmlConvert.ToTimeSpan(request.Timeout ?? "PT0S");
        }
        catch (FormatException)
        {
            throw OnvifFault.InvalidArgValue($"Timeout '{request.Timeout}' is not a duration.");
        }
        if (timeout > MaxPullTimeout) timeout = MaxPullTimeout;
        var time = context.PullPoints.Time;
        subscription.Extend();
        var notifications = await subscription.PullAsync(timeout, Math.Max(1, request.MessageLimit), context.Aborted);
        return new PullMessagesResponse
        {
            CurrentTime = time.GetUtcNow().UtcDateTime,
            TerminationTime = subscription.TerminationTime.UtcDateTime,
            NotificationMessage = notifications,
        };
    }

    public SetSynchronizationPointResponse SetSynchronizationPoint(SetSynchronizationPointRequest request)
    {
        EventHandlers.SubscriptionOf(context).Synchronize();
        return new SetSynchronizationPointResponse();
    }
}

// WS-BaseNotification's subscription manager at the same address: Renew and Unsubscribe.
[OnvifService(typeof(Wsdl.Wsn.SubscriptionManager), "event_service", 21, 12, Listed = false)]
public sealed class SubscriptionManagerHandlers(OnvifRequestContext context)
{
    public Wsdl.Wsn.RenewResponse Renew(Wsdl.Wsn.RenewRequest request)
    {
        var subscription = EventHandlers.SubscriptionOf(context);
        var time = context.PullPoints.Time;
        subscription.Extend(context.PullPoints.LifetimeOf(request.Renew?.TerminationTime));
        return new Wsdl.Wsn.RenewResponse(new RenewResponse
        {
            TerminationTime = subscription.TerminationTime.UtcDateTime,
            CurrentTime = time.GetUtcNow().UtcDateTime, CurrentTimeSpecified = true,
        });
    }

    public Wsdl.Wsn.UnsubscribeResponse Unsubscribe(Wsdl.Wsn.UnsubscribeRequest request)
    {
        context.PullPoints.Remove(EventHandlers.SubscriptionOf(context));
        return new Wsdl.Wsn.UnsubscribeResponse(new UnsubscribeResponse());
    }
}
