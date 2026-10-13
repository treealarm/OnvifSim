namespace OnvifSim.Onvif.Soap;

// An ONVIF error as a SOAP fault (ONVIF Core 5.11.2): env:Sender for a bad request, env:Receiver for
// what the device cannot do, refined by ter: subcodes. Handlers throw it; the dispatcher writes it.
public sealed class OnvifFault(bool isSender, string[] subcodes, string reason) : Exception(reason)
{
    public bool IsSender => isSender;
    // Outermost first, without the "ter:" prefix.
    public IReadOnlyList<string> Subcodes => subcodes;

    public static OnvifFault NotAuthorized() =>
        new(true, ["NotAuthorized"], "Sender not authorized.");

    public static OnvifFault ActionNotSupported(string operation) =>
        new(false, ["ActionNotSupported"], $"{operation} is not supported by this device.");

    public static OnvifFault InvalidArgValue(string reason) =>
        new(true, ["InvalidArgVal"], reason);

    public static OnvifFault NoProfile(string token) =>
        new(true, ["InvalidArgVal", "NoProfile"], $"No profile with token '{token}'.");

    public static OnvifFault NoConfig(string token) =>
        new(true, ["InvalidArgVal", "NoConfig"], $"No configuration with token '{token}'.");

    public static OnvifFault ConfigModify(string reason) =>
        new(true, ["InvalidArgVal", "ConfigModify"], reason);

    public static OnvifFault NoSuchService(string reason) =>
        new(true, ["InvalidArgVal", "NoSuchService"], reason);

    public static OnvifFault WellFormed(string reason) =>
        new(true, ["WellFormed"], reason);
}
