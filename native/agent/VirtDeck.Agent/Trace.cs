using System.Diagnostics;

namespace VirtDeck.Agent
{
    /// <summary>
    /// Timestamped lines on stderr when <c>VIRTDECK_AGENT_TRACE</c> is set in the environment, and
    /// nothing at all otherwise.
    ///
    /// <para>The agent runs on a host, over an SSH channel, with stderr as the only way to say
    /// anything, so this is the debugger. It is off by default because that stderr is also what the
    /// module quotes back to the user when a session fails to start.</para>
    /// </summary>
    internal static class Trace
    {
        private static readonly bool On = Environment.GetEnvironmentVariable("VIRTDECK_AGENT_TRACE") is { Length: > 0 };
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        public static bool Enabled => On;

        public static void Write(string message)
        {
            if (!On) return;
            Console.Error.WriteLine($"[{Clock.Elapsed.TotalMilliseconds,9:F1}] {message}");
            Console.Error.Flush();
        }
    }
}
