using System.Security.Cryptography;
using System.Xml;
using OnvifSim.Analytics;
using OnvifSim.Onvif.Wsdl;

namespace OnvifSim.Onvif.Events;

// One pull point subscription (ONVIF Core 9.1.2): the events a client asked for from one camera,
// queued until it pulls them. While it lives it keeps the camera's analytics looking and turns what
// they see into motion: on as soon as someone is in view, off once nobody has been for MotionHold, so
// a person on the edge of a house does not flicker the alarm.
public sealed class PullPoint : IDisposable
{
    public static readonly TimeSpan MotionHold = TimeSpan.FromSeconds(1);
    // Beyond this the oldest notifications go: a client that stopped pulling must not grow the server.
    private const int MaxQueued = 1000;

    private readonly Lock _gate = new();
    private readonly Queue<NotificationMessageHolderType> _queue = new();
    private readonly Func<string, bool> _wants;
    private readonly TimeProvider _time;
    private readonly CameraAnalytics.Subscription _looks;
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _motion;
    private DateTimeOffset _lastSeen = DateTimeOffset.MinValue;

    public PullPoint(string cameraId, Func<string, bool> wants, TimeSpan lifetime, CameraAnalytics analytics, TimeProvider time)
    {
        CameraId = cameraId;
        _time = time;
        Extend(lifetime);
        _wants = wants;
        // A new subscriber learns the current state of every property it asked for.
        Enqueue(MotionEvents.Notifications(false, "Initialized", time.GetUtcNow(), wants));
        _looks = analytics.Subscribe();
        _ = Task.Run(WatchAsync);
    }

    public string Id { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    public string CameraId { get; }

    public DateTimeOffset TerminationTime
    {
        get { lock (_gate) return field; }
        private set { lock (_gate) field = value; }
    }

    // How long the subscription lives from its last renewal: what the client last asked for.
    public TimeSpan Lifetime
    {
        get { lock (_gate) return field; }
        private set { lock (_gate) field = value; }
    }

    // Renew with a new lifetime, or keep the subscription alive for its lifetime again (a pull).
    public void Extend(TimeSpan? lifetime = null)
    {
        if (lifetime is { } asked) Lifetime = asked;
        TerminationTime = _time.GetUtcNow() + Lifetime;
    }

    // The current state again, as if newly subscribed (SetSynchronizationPoint).
    public void Synchronize()
    {
        bool motion;
        lock (_gate) motion = _motion;
        Enqueue(MotionEvents.Notifications(motion, "Initialized", _time.GetUtcNow(), _wants));
    }

    // Up to limit queued notifications, waiting up to timeout for the first one.
    public async Task<NotificationMessageHolderType[]> PullAsync(TimeSpan timeout, int limit, CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + timeout;
        while (true)
        {
            Task arrived;
            lock (_gate)
            {
                if (_queue.Count > 0)
                {
                    var taken = new List<NotificationMessageHolderType>();
                    while (_queue.Count > 0 && taken.Count < limit) taken.Add(_queue.Dequeue());
                    return [.. taken];
                }
                arrived = _arrived.Task;
            }
            var left = deadline - _time.GetUtcNow();
            if (left <= TimeSpan.Zero) return [];
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            var delay = Task.Delay(left, _time, wait.Token);
            await Task.WhenAny(arrived, delay);
            wait.Cancel();
            if (cancellationToken.IsCancellationRequested || _stop.IsCancellationRequested) return [];
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _looks.Dispose();
    }

    private async Task WatchAsync()
    {
        try
        {
            await foreach (var look in _looks.Frames.ReadAllAsync(_stop.Token))
            {
                if (look.Objects.Count > 0) _lastSeen = look.UtcTime;
                bool motion = look.UtcTime - _lastSeen < MotionHold;
                bool changed;
                lock (_gate)
                {
                    changed = motion != _motion;
                    _motion = motion;
                }
                if (changed) Enqueue(MotionEvents.Notifications(motion, "Changed", look.UtcTime, _wants));
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Enqueue(NotificationMessageHolderType[] notifications)
    {
        if (notifications.Length == 0) return;
        TaskCompletionSource arrived;
        lock (_gate)
        {
            foreach (var n in notifications) _queue.Enqueue(n);
            while (_queue.Count > MaxQueued) _queue.Dequeue();
            arrived = _arrived;
            _arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        arrived.TrySetResult();
    }
}

// Which topics a subscription's filter lets through (ConcreteSet dialect): wsnt:TopicExpression elements,
// each a "|"-separated list of topic paths, a path ending in "//." taking the whole subtree. The prefix is
// resolved where the client declared it. No expression means every topic; message content filters are
// not applied.
public static class TopicFilter
{
    private const string Wsnt = "http://docs.oasis-open.org/wsn/b-2";

    public static Func<string, bool> Of(FilterType? filter)
    {
        var expressions = (filter?.Any ?? [])
            .Where(e => e.LocalName == "TopicExpression" && e.NamespaceURI == Wsnt)
            .SelectMany(e => e.InnerText.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(text => Parse(e, text)))
            .ToList();
        if (expressions.Count == 0) return _ => true;
        return topic => expressions.Any(e =>
            e.Namespace == MotionEvents.TopicsNamespace && (topic == e.Path || (e.Subtree && topic.StartsWith(e.Path + "/", StringComparison.Ordinal))));
    }

    private static (string Namespace, string Path, bool Subtree) Parse(XmlElement element, string text)
    {
        bool subtree = text.EndsWith("//.", StringComparison.Ordinal);
        if (subtree) text = text[..^3];
        int colon = text.IndexOf(':');
        string prefix = colon < 0 ? "" : text[..colon];
        // A declaration on the envelope is not kept with the element; tns1 is ONVIF's own prefix anyway.
        string ns = element.GetNamespaceOfPrefix(prefix) is { Length: > 0 } declared ? declared
            : prefix == "tns1" ? MotionEvents.TopicsNamespace
            : "";
        return (ns, text[(colon + 1)..], subtree);
    }
}
