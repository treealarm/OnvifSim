using System.Text;

namespace OnvifSim.World;

// Rules for world file names: they are user-visible document names and file names at once, so they may
// be Cyrillic or contain spaces, but must be safe to store on every platform the simulator runs on.
public static class WorldNames
{
    public const int MaxLength = 80;

    private static readonly char[] InvalidCharacters = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    // Names Windows refuses to give a file, even with an extension after them.
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // The name as it is stored and shown, or the reason it cannot be a file name.
    public static string Normalize(string? name)
    {
        var normalized = name?.Trim().Normalize(NormalizationForm.FormC) ?? "";
        if (normalized.Length == 0)
            throw new InvalidWorldNameException("The name is empty.");
        if (normalized.Length > MaxLength)
            throw new InvalidWorldNameException($"The name is longer than {MaxLength} characters.");
        if (normalized.IndexOfAny(InvalidCharacters) >= 0)
            throw new InvalidWorldNameException("The name cannot contain / \\ : * ? \" < > |.");
        if (normalized.Any(char.IsControl))
            throw new InvalidWorldNameException("The name cannot contain control characters.");
        if (normalized is "." or ".." || normalized[0] == '.' || normalized[^1] == '.')
            throw new InvalidWorldNameException("The name cannot start or end with a dot.");
        var stem = normalized.Split('.', 2)[0];
        if (DeviceNames.Contains(stem))
            throw new InvalidWorldNameException($"'{stem}' is reserved by Windows.");
        return normalized;
    }

    public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // A free name close to the one asked for, for a file that must not overwrite another. The suffix
    // takes room from the base, so every result is still a valid, storable name.
    public static string Unique(string name, Func<string, bool> exists)
    {
        if (!exists(name)) return name;
        for (int i = 2; i < 1000; i++)
        {
            var suffix = $" ({i})";
            var candidate = name[..Math.Min(name.Length, MaxLength - suffix.Length)] + suffix;
            if (!exists(candidate)) return candidate;
        }
        throw new InvalidWorldNameException($"No free name is left for '{name}'.");
    }
}

// The name cannot be used as a world file name. A request error, not a fault.
public sealed class InvalidWorldNameException(string message) : Exception(message);

// A world with this name already exists, or a restore would overwrite one.
public sealed class WorldAlreadyExistsException(string message) : Exception(message);

// No world or trashed world has this name.
public sealed class WorldNotFoundException(string message) : Exception(message);
