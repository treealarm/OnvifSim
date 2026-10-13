namespace OnvifSim.Rendering;

// Turns the world and a camera pose into pixels. The software rasterizer is the only backend today;
// a GPU or game-engine backend can stand behind the same interface later.
public interface ICameraRenderer
{
    // Draws the scene and what moves in it (lit with RenderScene.Light) into frame.Rgba (Width * Height
    // * 4 bytes, top row first). Not thread-safe: use one renderer per thread.
    void Render(RenderScene scene, IReadOnlyList<RenderScene.Chunk> moving, in CameraPose pose, Frame frame);
}

public sealed class Frame(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Rgba { get; } = new byte[width * height * 4];
}
