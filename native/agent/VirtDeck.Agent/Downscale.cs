using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VirtDeck.Agent.X11;

namespace VirtDeck.Agent
{
    /// <summary>
    /// A BGRA picture made smaller the way a monitor with a scaling transform makes the X screen
    /// smaller: bilinear, sampled at the centre of each pixel it ends up with. All four bytes of a
    /// pixel are filtered, which is right for the screen (the fourth byte is padding) and for a
    /// cursor (its alpha is premultiplied).
    ///
    /// <para>The taps are worked out once per pair of sizes, so a frame costs four reads and three
    /// blends a byte. Each tap is kept as a left and a right index rather than a left index and a
    /// width, so the last column and the last row read nothing past the picture.</para>
    /// </summary>
    internal sealed class Downscale
    {
        private readonly int[] _left, _right, _across;
        private readonly int[] _top, _bottom, _down;

        public Downscale(int fromWidth, int fromHeight, int width, int height)
        {
            FromWidth = fromWidth;
            FromHeight = fromHeight;
            Width = width;
            Height = height;
            (_left, _right, _across) = Taps(fromWidth, width, 4);
            (_top, _bottom, _down) = Taps(fromHeight, height, fromWidth * 4);
        }

        public int FromWidth { get; }
        public int FromHeight { get; }
        public int Width { get; }
        public int Height { get; }

        /// <summary>
        /// For each pixel along one side: the byte offsets of the two source pixels either side of its
        /// centre, and the weight of the second out of 256.
        /// </summary>
        private static (int[] First, int[] Second, int[] Weight) Taps(int from, int to, int step)
        {
            var first = new int[to];
            var second = new int[to];
            var weight = new int[to];
            double ratio = (double)from / to;
            for (int i = 0; i < to; i++)
            {
                double at = Math.Max(0, (i + 0.5) * ratio - 0.5);
                int whole = Math.Min((int)at, from - 1);
                first[i] = whole * step;
                second[i] = Math.Min(whole + 1, from - 1) * step;
                weight[i] = whole == from - 1 ? 0 : (int)Math.Round((at - whole) * 256);
            }
            return (first, second, weight);
        }

        /// <summary>The pixels of the smaller picture that read anything from this rectangle of the larger.</summary>
        public XCapture.Rect Cover(XCapture.Rect source)
        {
            // Pixel i reads source columns floor(s) and floor(s) + 1, where s = (i + 0.5) * ratio - 0.5,
            // so it reaches [a, b) when a - 1 <= s < b. One more either side absorbs the rounding.
            var (x, right) = Span(source.X, source.X + source.Width, FromWidth, Width);
            var (y, bottom) = Span(source.Y, source.Y + source.Height, FromHeight, Height);
            return new XCapture.Rect(x, y, right - x, bottom - y);
        }

        private static (int Start, int End) Span(int a, int b, int from, int to)
        {
            double ratio = (double)from / to;
            int start = (int)Math.Floor((a - 0.5) / ratio - 0.5);
            int end = (int)Math.Ceiling((b + 0.5) / ratio - 0.5) + 1;
            return (Math.Clamp(start, 0, to), Math.Clamp(end, 0, to));
        }

        /// <summary>Where a pixel of the smaller picture is on the larger one.</summary>
        public int SourceX(int x) => Math.Clamp((int)((x + 0.5) * FromWidth / Width), 0, FromWidth - 1);

        public int SourceY(int y) => Math.Clamp((int)((y + 0.5) * FromHeight / Height), 0, FromHeight - 1);

        /// <summary>
        /// Fills this rectangle of <paramref name="to"/> from <paramref name="from"/>. The bounds are
        /// checked once, here, because the taps cannot leave the picture; the loop itself reads
        /// through refs, which takes a third off a whole frame (8 ms for 3072x1728 to 1920x1080).
        /// </summary>
        public void Resample(ReadOnlySpan<byte> from, Span<byte> to, XCapture.Rect rect)
        {
            if (from.Length < FromWidth * FromHeight * 4 || to.Length < Width * Height * 4 ||
                rect.X < 0 || rect.Y < 0 || rect.X + rect.Width > Width || rect.Y + rect.Height > Height)
                throw new ArgumentOutOfRangeException(nameof(rect));

            ref byte source = ref MemoryMarshal.GetReference(from);
            ref byte target = ref MemoryMarshal.GetReference(to);
            int stride = Width * 4;
            for (int j = rect.Y; j < rect.Y + rect.Height; j++)
            {
                ref byte top = ref Unsafe.Add(ref source, _top[j]);
                ref byte bottom = ref Unsafe.Add(ref source, _bottom[j]);
                int lower = _down[j], upper = 256 - lower;
                ref byte row = ref Unsafe.Add(ref target, j * stride);

                for (int i = rect.X; i < rect.X + rect.Width; i++)
                {
                    int right = _across[i], left = 256 - right;
                    ref byte a = ref Unsafe.Add(ref top, _left[i]);
                    ref byte b = ref Unsafe.Add(ref top, _right[i]);
                    ref byte c = ref Unsafe.Add(ref bottom, _left[i]);
                    ref byte d = ref Unsafe.Add(ref bottom, _right[i]);
                    ref byte o = ref Unsafe.Add(ref row, i * 4);
                    o = Blend(a, b, c, d, left, right, upper, lower);
                    Unsafe.Add(ref o, 1) = Blend(Unsafe.Add(ref a, 1), Unsafe.Add(ref b, 1), Unsafe.Add(ref c, 1), Unsafe.Add(ref d, 1), left, right, upper, lower);
                    Unsafe.Add(ref o, 2) = Blend(Unsafe.Add(ref a, 2), Unsafe.Add(ref b, 2), Unsafe.Add(ref c, 2), Unsafe.Add(ref d, 2), left, right, upper, lower);
                    Unsafe.Add(ref o, 3) = Blend(Unsafe.Add(ref a, 3), Unsafe.Add(ref b, 3), Unsafe.Add(ref c, 3), Unsafe.Add(ref d, 3), left, right, upper, lower);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte Blend(byte a, byte b, byte c, byte d, int left, int right, int upper, int lower) =>
            (byte)(((a * left + b * right) * upper + (c * left + d * right) * lower + 32768) >> 16);
    }
}
