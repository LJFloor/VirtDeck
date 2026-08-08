namespace VirtDeck.Unattend
{
    /// <summary>
    /// The "Remove bloatware" tab: which of the generator's sixty-odd removals to run.
    ///
    /// Stored as ids because the list is the generator's, not VirtDeck's, and it grows and shrinks
    /// with every Windows release. An id a later build no longer knows is dropped on the way to the
    /// generator rather than failing the load, which is the right trade for a set: losing one removal
    /// from a preset is a smaller harm than refusing to open it.
    /// </summary>
    public sealed class BloatwareConfig
    {
        public List<string> RemoveIds { get; set; } = new();
    }
}
