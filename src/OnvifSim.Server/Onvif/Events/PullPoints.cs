using System.Collections.Concurrent;
using OnvifSim.Analytics;
using OnvifSim.Onvif.Soap;
using OnvifSim.Onvif.Wsdl;

namespace OnvifSim.Onvif.Events;

// Every camera's pull point subscriptions. A subscription lives until its termination time, which
// Renew and every PullMessages move on; one that runs out is dropped within SweepInterval, and its
// camera's analytics stop when nothing else needs them.
public sealed class PullPoints : IDisposable
{
    public const int MaxPerCamera = 10;
    // What a subscription lives without an InitialTerminationTime, and the most it may ask for.
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, PullPoint> _subscriptions = new();
    private readonly AnalyticsManager _analytics;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ITimer _sweep;

    public PullPoints(AnalyticsManager analytics, TimeProvider time, ILogger<PullPoints> logger)
    {
        _analytics = analytics;
        _time = time;
        _logger = logger;
        _sweep = time.CreateTimer(_ => Sweep(), null, SweepInterval, SweepInterval);
    }

    public TimeProvider Time => _time;

    public PullPoint Create(string cameraId, FilterType? filter, TimeSpan lifetime)
    {
        if (_subscriptions.Values.Count(s => s.CameraId == cameraId) >= MaxPerCamera)
            throw new OnvifFault(false, ["Action", "SubscribeCreationFailed"], $"The camera keeps at most {MaxPerCamera} pull points.");
        var analytics = _analytics.Get(cameraId) ?? throw OnvifFault.InvalidArgValue($"No camera '{cameraId}'.");
        var subscription = new PullPoint(cameraId, TopicFilter.Of(filter), lifetime, analytics, _time);
        _subscriptions[subscription.Id] = subscription;
        _logger.LogInformation("Camera {Camera}: pull point {Subscription} until {Termination:u}", cameraId, subscription.Id, subscription.TerminationTime);
        return subscription;
    }

    // The camera's subscription with the id the request addressed; ResourceUnknown otherwise, which
    // tells a client to subscribe again.
    public PullPoint Find(string cameraId, string? id) =>
        id != null && _subscriptions.TryGetValue(id, out var subscription) && subscription.CameraId == cameraId
            ? subscription
            : throw new OnvifFault(true, ["InvalidArgVal", "ResourceUnknown"], $"No subscription '{id}'.");

    public void Remove(PullPoint subscription)
    {
        if (!_subscriptions.TryRemove(subscription.Id, out _)) return;
        subscription.Dispose();
        _logger.LogInformation("Camera {Camera}: pull point {Subscription} ended", subscription.CameraId, subscription.Id);
    }

    // A requested lifetime (InitialTerminationTime, Renew's TerminationTime): an xs:duration or an
    // absolute xs:dateTime, kept within MaxLifetime.
    public TimeSpan LifetimeOf(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return DefaultLifetime;
        TimeSpan lifetime;
        try
        {
            lifetime = requested.TrimStart().StartsWith('P')
                ? System.Xml.XmlConvert.ToTimeSpan(requested.Trim())
                : System.Xml.XmlConvert.ToDateTimeOffset(requested.Trim()) - _time.GetUtcNow();
        }
        catch (FormatException)
        {
            throw new OnvifFault(true, ["InvalidArgVal", "UnacceptableInitialTerminationTime"], $"'{requested}' is neither a duration nor a time.");
        }
        if (lifetime <= TimeSpan.Zero)
            throw new OnvifFault(true, ["InvalidArgVal", "UnacceptableInitialTerminationTime"], $"'{requested}' is already past.");
        return lifetime < MaxLifetime ? lifetime : MaxLifetime;
    }

    public void Dispose()
    {
        _sweep.Dispose();
        foreach (var subscription in _subscriptions.Values) Remove(subscription);
    }

    private void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var subscription in _subscriptions.Values)
            if (subscription.TerminationTime <= now) Remove(subscription);
    }
}
