namespace VirtDeck.Services
{
    /// <summary>
    /// A running command whose stdin, stdout and stderr belong to this end: what
    /// <see cref="SshPipe"/> is over SSH, and what a locally started process is when the Remote
    /// Control module is being developed against this machine's own display.
    ///
    /// <para>It exists so the one caller that does not care which it is (the remote desktop) can be
    /// handed either. Everything else uses <see cref="SshPipe"/> directly.</para>
    /// </summary>
    public interface ICommandPipe : IDisposable
    {
        /// <summary>The command's stdout. Blocking reads; 0 at end of stream.</summary>
        Stream Output { get; }

        /// <summary>The command's stdin. Writes may block, so they belong on a thread of their own.</summary>
        Stream Input { get; }

        /// <summary>The exit status once it has ended, null when it ended without one.</summary>
        Task<int?> Completion { get; }

        /// <summary>The last of what the command wrote to stderr, which is what a caller quotes when it fails.</summary>
        string StderrTail { get; }
    }
}
