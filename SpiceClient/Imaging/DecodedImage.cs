namespace SpiceClient.Imaging;

/// <summary>
/// A decoded image in top-down BGRA byte order (stride = Width*4), the same
/// byte order as the framebuffer (Format32bppArgb), so blits are a direct copy.
/// </summary>
public sealed class DecodedImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }

    public DecodedImage(int width, int height, byte[] bgra)
    {
        Width = width;
        Height = height;
        Bgra = bgra;
    }
}
