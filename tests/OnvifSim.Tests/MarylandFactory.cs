using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using OnvifSim.World;

namespace OnvifSim.Tests;

// A simulator started on the world the demo uses, the way the deployment does it: the world file sits
// in the data directory and the active marker names it, so the cameras served from the first request
// are Maryland's, not the default village's.
public sealed class MarylandFactory : SimFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var worlds = Path.Combine(Data.Path, "worlds");
        Directory.CreateDirectory(worlds);
        File.WriteAllText(Path.Combine(worlds, MarylandWorld.Name + ".json"),
            JsonSerializer.Serialize(MarylandWorld.Create(), WorldStore.Json));
        File.WriteAllText(Path.Combine(Data.Path, "active-world.json"),
            JsonSerializer.Serialize(new { version = 1, name = MarylandWorld.Name }, WorldStore.Json));
        base.ConfigureWebHost(builder);
    }
}
