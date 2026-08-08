using System.Text.Json;
using System.Text.Json.Serialization;

namespace VirtDeck.Unattend
{
    /// <summary>
    /// Reads and writes the answer-file window's own settings file.
    ///
    /// It exists because the generator has no reader. The online tool can re-import a file it generated,
    /// but that is server-side code in its web front end, not part of the library, and writing one here
    /// would mean maintaining a parser for every element twenty-odd tabs can emit, against a library
    /// that changes. A settings file of VirtDeck's own is a full-fidelity round trip for a fraction of
    /// that, and it can hold what the answer file cannot: the values sitting under the radio options the
    /// user is not currently on.
    ///
    /// So the two exports are different things and are labelled as such. "Export XML" writes the answer
    /// file, which is what the disc carries and what Windows reads. A preset is what this window reads.
    /// </summary>
    public static class UnattendPreset
    {
        /// <summary>Bumped only when a change cannot be expressed as added or ignored properties.</summary>
        public const int CurrentVersion = 1;

        /// <summary>
        /// Enums are written by name, unlike <c>AppSettings</c>, which writes them numerically because a
        /// settings file that fails to parse must never stop the app launching. The trade is reversed
        /// here: a preset is user-chosen, so a failure is reportable in a dialog, and it is a file
        /// somebody may open and edit, where a bare number would be unreadable and would silently change
        /// meaning if an enum member were ever inserted.
        /// </summary>
        internal static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public static byte[] Serialize(UnattendConfig config) =>
            JsonSerializer.SerializeToUtf8Bytes(config, Json);

        /// <exception cref="InvalidDataException">Not a preset, or one this build cannot read.</exception>
        /// <exception cref="JsonException">Malformed JSON.</exception>
        public static UnattendConfig Parse(byte[] json)
        {
            // Deserializing straight away would accept any JSON object at all and answer with a config
            // of pure defaults, which would look like a successful import that quietly blanked every
            // page. Same reason the reader this replaces insisted the root element was <unattend>.
            int version;
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !TryGetVersion(doc.RootElement, out version))
                {
                    throw new InvalidDataException(
                        "This is not a VirtDeck answer-file preset. Presets are the .json files this " +
                        "window writes with \"Save preset\"; an autounattend.xml cannot be read back in.");
                }
            }

            if (version > CurrentVersion)
            {
                throw new InvalidDataException(
                    $"This preset is in format {version}, and this build of VirtDeck understands " +
                    $"format {CurrentVersion}. Some of it would be dropped without saying so, so it " +
                    "was not opened.");
            }

            return JsonSerializer.Deserialize<UnattendConfig>(json, Json)
                   ?? throw new InvalidDataException("The preset is empty.");
        }

        private static bool TryGetVersion(JsonElement root, out int version)
        {
            version = 0;
            foreach (var property in root.EnumerateObject())
            {
                if (!property.NameEquals(nameof(UnattendConfig.Version)) &&
                    !string.Equals(property.Name, nameof(UnattendConfig.Version),
                                   StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                return property.Value.TryGetInt32(out version);
            }
            return false;
        }
    }
}
