using System.Security.Cryptography;
using System.Xml;
using OnvifSim.Analytics;
using OnvifSim.Onvif.Wsdl;
using OnvifSim.World;

namespace OnvifSim.Onvif.Events;

// One pull point subscription (ONVIF Core 9.1.2): the events a client asked for from one camera,
// queued until it pulls them. While it lives it keeps the camera's analytics looking and turns what
// they see into motion: on as soon as something is in view, off once nothing has been for MotionHold, so
// a person on the edge of a house does not flicker the alarm. It also reports the camera's relay outputs
// and digital inputs, whose state changes when the world does.
public sealed class PullPoint : IDisposable
{
    public static readonly TimeSpan MotionHold = TimeSpan.FromSeconds(1);
    // Beyond this the oldest notifications go: a client that stopped pulling must not grow the server.
    private const int MaxQueued = 1000;

    private readonly Lock _gate = new();
    private readonly Queue<NotificationMessageHolderType> _queue = new();
    private readonly Func<string, bool> _wants;
    private readonly TimeProvider _time;
    private readonly WorldStore _store;
    private readonly Action<WorldModel> _onWorldChanged;
    private readonly CameraAnalytics.Subscription _looks;
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _motion;
    private DateTimeOffset _lastSeen = DateTimeOffset.MinValue;
    // The state each device of the camera was last seen in, to tell what changed.
    private readonly Dictionary<(string Topic, string Token), DeviceReading> _devices = [];

    public PullPoint(string cameraId, Func<string, bool> wants, TimeSpan lifetime, CameraAnalytics analytics, TimeProvider time, WorldStore store)
    {
        CameraId = cameraId;
        _time = time;
        _store = store;
        Extend(lifetime);
        _wants = wants;
        // A new subscriber learns the current state of every property it asked for, devices included.
        Enqueue(MotionEvents.Notifications(false, "Initialized", time.GetUtcNow(), wants));
        Enqueue(DeviceStates(_store.Current, "Initialized"));
        _onWorldChanged = OnWorldChanged;
        _store.Changed += _onWorldChanged;
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

    // The current state again, as if newly subscribed (SetSynchronizationPoint). Device states are read
    // from the world now, so a change this point missed is repaired here.
    public void Synchronize()
    {
        bool motion;
        lock (_gate) motion = _motion;
        Enqueue(MotionEvents.Notifications(motion, "Initialized", _time.GetUtcNow(), _wants));
        Enqueue(DeviceStates(_store.Current, "Initialized"));
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
        _store.Changed -= _onWorldChanged;
        _stop.Cancel();
        _looks.Dispose();
    }

    // Every relay and every wired input of the camera as a notification, recording what each reads now.
    private NotificationMessageHolderType[] DeviceStates(WorldModel world, string propertyOperation)
    {
        var notices = new List<NotificationMessageHolderType>();
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var (topic, token, reading) in States(world))
            {
                _devices[(topic, token)] = reading;
                if (_wants(topic))
                    notices.Add(DeviceEvents.Notification(topic, token, reading.State, reading.ContactClosed, propertyOperation, now));
            }
        }
        return [.. notices];
    }

    // The world changed: whichever device of the camera now reads differently is reported as Changed, and
    // one that appeared while subscribed gets its first state as Initialized.
    private void OnWorldChanged(WorldModel world)
    {
        var notices = new List<NotificationMessageHolderType>();
        lock (_gate)
        {
            var seen = new HashSet<(string Topic, string Token)>();
            var now = _time.GetUtcNow();
            foreach (var (topic, token, reading) in States(world))
            {
                var key = (topic, token);
                seen.Add(key);
                bool known = _devices.TryGetValue(key, out var previous);
                // A relay's contact may change while its logical state does not: IdleState is a setting.
                if (known && previous == reading) continue;
                _devices[key] = reading;
                if (_wants(topic))
                    notices.Add(DeviceEvents.Notification(topic, token, reading.State, reading.ContactClosed,
                        known ? "Changed" : "Initialized", now));
            }
            foreach (var key in _devices.Keys.Where(key => !seen.Contains(key)).ToList()) _devices.Remove(key);
        }
        Enqueue([.. notices]);
    }

    // A relay's logical state and contact, and an input's state as its wire gives it; an input no wire
    // drives has none.
    private IEnumerable<(string Topic, string Token, DeviceReading Reading)> States(WorldModel world)
    {
        var camera = DeviceIo.FindCamera(world, CameraId);
        if (camera is null) yield break;
        foreach (var relay in camera.RelayOutputs)
            yield return (DeviceEvents.RelayTopic, relay.Token, new DeviceReading(relay.Active, DeviceIo.ContactClosed(relay)));
        foreach (var input in camera.DigitalInputs)
            if (DeviceIo.InputActive(world, CameraId, input.Token) is { } state)
                yield return (DeviceEvents.DigitalInputTopic, input.Token, new DeviceReading(state, null));
    }

    // What a device reads now: its logical state, and for a relay the physical one as well.
    private readonly record struct DeviceReading(bool State, bool? ContactClosed);

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
