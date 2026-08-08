using System.Collections.Immutable;
using Schneegans.Unattend;

namespace VirtDeck.Unattend
{
    /// <summary>
    /// One entry of one of the generator's lookup tables, reduced to the two things a picker needs.
    ///
    /// <see cref="ToString"/> is overridden on purpose, and this is deliberately a class rather than a
    /// record: a record's generated <c>ToString</c> prints the whole shape, which would force a
    /// <c>DataTemplate</c> with a <c>DataType=</c> on every lookup <c>ComboBox</c> in the answer-file
    /// window, since the UI project compiles its bindings. With this one line a bare
    /// <c>&lt;ComboBox ItemsSource="..."/&gt;</c> renders correctly.
    /// </summary>
    public sealed class UnattendOption
    {
        public UnattendOption(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName;
        }

        /// <summary>The generator's own key, and the only form VirtDeck ever stores or serialises.</summary>
        public string Id { get; }

        public string DisplayName { get; }

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// The one place VirtDeck talks to <c>Schneegans.Unattend</c>'s data tables, and the only place the
    /// generator is constructed. Everything above <c>VirtDeck.Core</c> sees <see cref="UnattendOption"/>
    /// lists and nothing else, the same way <c>FileDialogs</c> and <c>DropFiles</c> are the single
    /// translation point for their dialects.
    ///
    /// Construction parses about 280 KB of embedded JSON into ten tables, so it happens once per
    /// process. Nothing here is per-VM or per-window state.
    /// </summary>
    public static class UnattendCatalog
    {
        private static readonly Lazy<Tables> Loaded =
            new(() => new Tables(), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// The live generator. Internal on purpose: <see cref="UnattendXml"/> and
        /// <see cref="UnattendConfigMapper"/> are the only callers, and keeping it out of the UI is what
        /// keeps <c>Schneegans.Unattend</c> types out of every view.
        /// </summary>
        internal static UnattendGenerator Generator => Loaded.Value.Generator;

        /// <summary>
        /// Forces the tables to load. Call it off the UI thread while the user is still doing something
        /// else, so the first click of "Customize Windows setup..." does not pay for the JSON parse.
        /// Safe to call any number of times, and safe to call concurrently with a real use.
        /// </summary>
        public static void Prime() => _ = Loaded.Value;

        /// <summary>Windows Setup UI languages, for the "install using these language settings" picker.</summary>
        public static IReadOnlyList<UnattendOption> ImageLanguages => Loaded.Value.ImageLanguages;

        /// <summary>Locales, which carry the display language, formats and a default keyboard.</summary>
        public static IReadOnlyList<UnattendOption> UserLocales => Loaded.Value.UserLocales;

        /// <summary>Keyboard layouts and IMEs, keyed by the eight-digit layout id Windows uses.</summary>
        public static IReadOnlyList<UnattendOption> KeyboardIdentifiers => Loaded.Value.KeyboardIdentifiers;

        /// <summary>Home locations, keyed by GeoID.</summary>
        public static IReadOnlyList<UnattendOption> GeoLocations => Loaded.Value.GeoLocations;

        /// <summary>Time zones, keyed by the registry key name ("W. Europe Standard Time").</summary>
        public static IReadOnlyList<UnattendOption> TimeOffsets => Loaded.Value.TimeOffsets;

        /// <summary>Windows editions that have a generic product key. Hidden editions are dropped.</summary>
        public static IReadOnlyList<UnattendOption> WindowsEditions => Loaded.Value.WindowsEditions;

        /// <summary>Everything the generator knows how to uninstall.</summary>
        public static IReadOnlyList<UnattendOption> Bloatwares => Loaded.Value.Bloatwares;

        /// <summary>The icons that can be shown on the desktop.</summary>
        public static IReadOnlyList<UnattendOption> DesktopIcons => Loaded.Value.DesktopIcons;

        /// <summary>The folders that can be pinned beside the Start menu's power button.</summary>
        public static IReadOnlyList<UnattendOption> StartFolders => Loaded.Value.StartFolders;

        /// <summary>Answer-file components, for the raw-XML escape hatch.</summary>
        public static IReadOnlyList<UnattendOption> Components => Loaded.Value.Components;

        /// <summary>
        /// The generator plus every projection of its tables, built together so that no property can
        /// race another into a half-built cache.
        /// </summary>
        private sealed class Tables
        {
            internal Tables()
            {
                Generator = new UnattendGenerator();

                ImageLanguages = Project(Generator.ImageLanguages, x => x.DisplayName);
                UserLocales = Project(Generator.UserLocales, x => x.DisplayName);
                KeyboardIdentifiers = Project(Generator.KeyboardIdentifiers, x => x.DisplayName);
                GeoLocations = Project(Generator.GeoLocations, x => x.DisplayName);
                TimeOffsets = Project(Generator.TimeOffsets, x => x.DisplayName);
                Bloatwares = Project(Generator.Bloatwares, x => x.DisplayName);
                DesktopIcons = Project(Generator.DesktopIcons, x => x.DisplayName);
                StartFolders = Project(Generator.StartFolders, x => x.DisplayName);
                // A component has no display name; its id is what the Microsoft docs call it.
                Components = Project(Generator.Components, x => x.Id);
                // The invisible editions are the ones with no generic key to offer.
                WindowsEditions = Project(
                    Generator.WindowsEditions.Values.Where(e => e.Visible), x => x.DisplayName);
            }

            internal UnattendGenerator Generator { get; }
            internal IReadOnlyList<UnattendOption> ImageLanguages { get; }
            internal IReadOnlyList<UnattendOption> UserLocales { get; }
            internal IReadOnlyList<UnattendOption> KeyboardIdentifiers { get; }
            internal IReadOnlyList<UnattendOption> GeoLocations { get; }
            internal IReadOnlyList<UnattendOption> TimeOffsets { get; }
            internal IReadOnlyList<UnattendOption> WindowsEditions { get; }
            internal IReadOnlyList<UnattendOption> Bloatwares { get; }
            internal IReadOnlyList<UnattendOption> DesktopIcons { get; }
            internal IReadOnlyList<UnattendOption> StartFolders { get; }
            internal IReadOnlyList<UnattendOption> Components { get; }

            private static IReadOnlyList<UnattendOption> Project<T>(
                IImmutableDictionary<string, T> table, Func<T, string> label) where T : IKeyed =>
                Project(table.Values, label);

            private static IReadOnlyList<UnattendOption> Project<T>(
                IEnumerable<T> values, Func<T, string> label) where T : IKeyed =>
                values.Select(v => new UnattendOption(v.Id, label(v)))
                      .OrderBy(o => o.DisplayName, StringComparer.CurrentCulture)
                      .ToList();
        }
    }
}
