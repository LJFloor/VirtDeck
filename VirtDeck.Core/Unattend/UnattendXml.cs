using Schneegans.Unattend;

namespace VirtDeck.Unattend
{
    /// <summary>
    /// A config the generator refused. The message is meant to be shown to the user as it is: for the
    /// validation cases it is the generator's own wording, which is written for humans.
    /// </summary>
    public sealed class UnattendBuildException : Exception
    {
        public UnattendBuildException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Turns what the answer-file window collected into the bytes of an <c>autounattend.xml</c>.
    ///
    /// This is the boundary: above it nothing knows the generator exists, and the only exception that
    /// escapes is <see cref="UnattendBuildException"/>. That matters because an invalid config is now
    /// reachable at all. The hand-written writer this replaced could not fail, so no caller had a
    /// failure path; the generator validates its input in the settings constructors and validates its
    /// own output against the unattend schema, and either can say no.
    ///
    /// The bytes are ASCII with CRLF line endings, which is what the generator emits and what Setup's
    /// parser expects. Non-ASCII characters become numeric character references. Do not "fix" that: the
    /// whole template and its schema were verified upstream against exactly this output.
    /// </summary>
    public static class UnattendXml
    {
        /// <exception cref="UnattendBuildException">The config cannot be turned into an answer file.</exception>
        public static byte[] Build(UnattendConfig config)
        {
            try
            {
                var generator = UnattendCatalog.Generator;
                return UnattendGenerator.Serialize(
                    generator.GenerateXml(UnattendConfigMapper.ToConfiguration(config, generator)));
            }
            catch (ConfigurationException ex)
            {
                // Upstream's validation messages name the setting and the value; pass them through
                // untouched rather than wrapping them in wording of our own.
                throw new UnattendBuildException(ex.Message, ex);
            }
            catch (Exception ex)
            {
                // Everything else: a schema violation, a malformed fragment the user pasted, or a bug.
                // The user still gets a dialog instead of a crash, and the inner exception carries the
                // detail for the log window.
                throw new UnattendBuildException(
                    "The answer file could not be generated:\n\n" + ex.Message, ex);
            }
        }
    }
}
