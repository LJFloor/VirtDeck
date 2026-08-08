namespace VirtDeck.Unattend
{
    /// <summary>
    /// The "Activation" and "Processor architectures" tab: which key activates the installed system,
    /// and which architectures the answer file is written for.
    ///
    /// This is not the same thing as the edition Setup installs. That is chosen on the Windows PE page,
    /// where the product key doubles as the edition selector; this key is the one applied afterwards,
    /// and the reference tool keeps them apart for the same reason.
    /// </summary>
    public sealed class ActivationConfig
    {
        public bool UseProductKey { get; set; }

        /// <summary>Five groups of five, checked by the generator, which rejects anything else.</summary>
        public string ProductKey { get; set; } = "";

        /// <summary>
        /// The architectures each <c>component</c> element is emitted for. At least one is required.
        /// x64 alone is the default and covers every VM this wizard defines; the other two are here
        /// because an answer file can be reused elsewhere.
        /// </summary>
        public bool Amd64 { get; set; } = true;

        public bool X86 { get; set; }

        public bool Arm64 { get; set; }
    }
}
