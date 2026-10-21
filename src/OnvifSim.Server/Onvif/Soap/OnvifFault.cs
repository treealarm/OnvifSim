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

    public static OnvifFault NoToken(string token) =>
        new(true, ["InvalidArgVal", "NoToken"], $"No preset with token '{token}'.");

    public static OnvifFault NoEntity(string token) =>
        new(true, ["InvalidArgVal", "NoEntity"], $"No PTZ node with token '{token}'.");

    public static OnvifFault SpaceNotSupported(string space) =>
        new(true, ["InvalidArgVal", "SpaceNotSupported"], $"Space '{space}' is not supported; use the generic spaces.");

    public static OnvifFault InvalidPosition(string reason) =>
        new(true, ["InvalidArgVal", "InvalidPosition"], reason);

    public static OnvifFault InvalidTranslation(string reason) =>
        new(true, ["InvalidArgVal", "InvalidTranslation"], reason);

    public static OnvifFault InvalidVelocity(string reason) =>
        new(true, ["InvalidArgVal", "InvalidVelocity"], reason);

    public static OnvifFault InvalidSpeed(string reason) =>
        new(true, ["InvalidArgVal", "InvalidSpeed"], reason);

    public static OnvifFault TimeoutNotSupported(string reason) =>
        new(true, ["InvalidArgVal", "TimeoutNotSupported"], reason);

    public static OnvifFault PresetExist(string name) =>
        new(true, ["InvalidArgVal", "PresetExist"], $"Another preset is already named '{name}'.");

    public static OnvifFault InvalidPresetName(string reason) =>
        new(true, ["InvalidArgVal", "InvalidPresetName"], reason);

    public static OnvifFault TooManyPresets(int maximum) =>
        new(false, ["Action", "TooManyPresets"], $"The camera holds at most {maximum} presets.");

    public static OnvifFault NoSource(string token) =>
        new(true, ["InvalidArgVal", "NoSource"], $"No video source with token '{token}'.");

    public static OnvifFault SettingsInvalid(string reason) =>
        new(true, ["InvalidArgVal", "SettingsInvalid"], reason);

    public static OnvifFault NoRecording(string token) =>
        new(true, ["InvalidArgVal", "NoRecording"], $"No recording with token '{token}'.");

    public static OnvifFault NoRecordingJob(string token) =>
        new(true, ["InvalidArgVal", "NoRecordingJob"], $"No recording job with token '{token}'.");

    public static OnvifFault BadMode(string mode) =>
        new(true, ["InvalidArgVal", "BadMode"], $"Recording job mode '{mode}' is neither Active nor Idle.");

    public static OnvifFault InvalidToken(string token) =>
        new(true, ["InvalidArgVal", "InvalidToken"], $"No search with token '{token}'.");
}
