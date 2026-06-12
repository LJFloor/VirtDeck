namespace SpiceClient;

/// <summary>
/// Process-wide counter of bytes transferred over SPICE channel sockets (both directions,
/// aggregated across every channel of every open console). The WinForms app samples this to
/// fold SPICE console traffic into its SSH-tunnel throughput readout — SSH.NET's forwarded
/// ports expose no counters, but our own <see cref="Transport.ChannelSocket"/> carries the
/// exact forwarded payload.
/// </summary>
public static class SpiceTraffic
{
    private static long _bytes;
    public static long BytesTransferred => Interlocked.Read(ref _bytes);
    internal static void Add(long n) => Interlocked.Add(ref _bytes, n);
}
