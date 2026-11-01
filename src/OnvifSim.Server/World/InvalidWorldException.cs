namespace OnvifSim.World;

// A world that was loaded is unusable: a setting outside its range, an object type that does not exist,
// two cameras asking for one port. Thrown before a world replaces the current one, so it stays intact.
public sealed class InvalidWorldException(string message) : Exception(message);
