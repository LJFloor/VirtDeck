namespace VmManager.Models
{
    public class VmInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Uuid { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public int VCpus { get; set; }
        public string Memory { get; set; } = string.Empty;
        public string Uptime { get; set; } = "N/A";
    }
}
