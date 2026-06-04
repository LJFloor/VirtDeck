namespace VmManager.Models
{
    /// <summary>One entry in a remote directory listing (from VirshService.ListDirectory).</summary>
    public class RemoteEntry
    {
        public string Name { get; set; } = string.Empty;
        public bool IsDir { get; set; }
        public long Size { get; set; }
        public string Modified { get; set; } = string.Empty;
    }
}
