namespace VirtDeck.Services
{
    /// <summary>
    /// The arithmetic every sampler in the app does between two readings of a kernel counter.
    ///
    /// <para>Lifted out of <see cref="HostMetricsService"/> when the network module's traffic tail
    /// needed the same thing per interface. One copy, because the rule inside it is the kind that
    /// drifts: a counter that went backwards wrapped or was reset under a reboot, and either is
    /// better reported as nothing than drawn as a spike.</para>
    /// </summary>
    public static class Counters
    {
        /// <summary>
        /// Units per second between two readings of one counter, or 0 when it went backwards. The
        /// time is the host's own uptime, so clock skew between the host and this PC never enters it.
        /// </summary>
        public static double Rate(long now, long before, double seconds) =>
            seconds > 0 && now >= before ? (now - before) / seconds : 0;
    }
}
