namespace SpiceClient;

/// <summary>
/// A decoded SPICE cursor shape (ALPHA type): straight (non-premultiplied) BGRA
/// pixels, top-down, with the hotspot. The UI layer turns this into a native
/// Windows cursor.
/// </summary>
public sealed class CursorShape
{
    public int Width { get; }
    public int Height { get; }
    public int HotX { get; }
    public int HotY { get; }
    public byte[] Bgra { get; }

    public CursorShape(int width, int height, int hotX, int hotY, byte[] bgra)
    {
        Width = width;
        Height = height;
        HotX = hotX;
        HotY = hotY;
        Bgra = bgra;
    }
}
