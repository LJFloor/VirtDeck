namespace VirtDeck.Models
{
    /// <summary>
    /// What kind of control one setting is edited with.
    ///
    /// <para>The page is built from this rather than from the manager's identity, which is what keeps
    /// the view from ever naming apt, dnf or pacman: everything those three disagree about is a
    /// value in the catalog their own class builds. Adding a setting is a line in a manager, not a
    /// row of markup and a handler.</para>
    /// </summary>
    public enum SettingKind
    {
        /// <summary>A <c>CheckBox</c>. <see cref="PackageSetting.Value"/> is "1" or "0".</summary>
        Toggle,

        /// <summary>A <c>NumericUpDown</c> between <see cref="PackageSetting.Min"/> and <see cref="PackageSetting.Max"/>.</summary>
        Number,

        /// <summary>A <c>ComboBox</c> over <see cref="PackageSetting.Choices"/>.</summary>
        Choice,

        /// <summary>A <c>TextBox</c>, for a value with no shape worth enforcing here.</summary>
        Text,
    }

    /// <summary>
    /// One option of a <see cref="SettingKind.Choice"/>. <paramref name="Value"/> is the spelling the
    /// host's own config file uses and <paramref name="Label"/> is the sentence a person reads, which
    /// are deliberately different things: <c>when-needed</c> is what dnf wants written and "When
    /// something needs it" is what the row should say.
    /// </summary>
    public sealed record SettingChoice(string Value, string Label);

    /// <summary>
    /// One editable setting of the host's package manager.
    ///
    /// <para><b><see cref="Value"/> is always the wire spelling, never a display string.</b> The
    /// control renders it and the save writes it back untranslated, so there is exactly one place
    /// each tool's vocabulary is known: that tool's own class. A display string reaching this
    /// property is how a save comes to write "Every 2 days" into a file expecting a "2".</para>
    /// </summary>
    public class PackageSetting
    {
        /// <summary>
        /// How the manager names this setting to itself. Opaque to everything above
        /// <c>IPackageManager</c>: it is what comes back in a <see cref="PackageSettingChange"/> and
        /// what the save script switches on, so it may be a config key, a unit name or a word that
        /// stands for several of both (dnf's automatic-updates choice is one key over three effects).
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>The label in the first grid column.</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>The tooltip, or empty. Where the underlying key is worth naming, it is named here.</summary>
        public string Description { get; set; } = string.Empty;

        public SettingKind Kind { get; set; } = SettingKind.Toggle;

        /// <summary>The host's current value, in the host's own spelling. See the class remarks.</summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>The options of a <see cref="SettingKind.Choice"/>, and empty for every other kind.</summary>
        public IReadOnlyList<SettingChoice> Choices { get; set; } = Array.Empty<SettingChoice>();

        public int Min { get; set; }
        public int Max { get; set; } = int.MaxValue;

        /// <summary>The unit drawn beside a <see cref="SettingKind.Number"/> ("seconds"), or empty.</summary>
        public string Unit { get; set; } = string.Empty;

        /// <summary>
        /// Why this one row cannot be changed on this host, or empty. A disabled control is not
        /// hit-testable in Avalonia, so the page hangs this off an enclosing border: same workaround
        /// as the Install security updates button one tab over.
        /// </summary>
        public string UnavailableReason { get; set; } = string.Empty;

        /// <summary>
        /// Drawn, and its value stated, but never written. Three things reach it, and they are all the
        /// same refusal: a value that explains what the host will do and that this app has no business
        /// editing from here (apt's allowed origins); a value in a spelling the control cannot hold
        /// without rounding it (an apt interval already set to <c>always</c> or <c>4h</c>); and a unit
        /// systemd will not let anybody enable (<c>static</c>, <c>masked</c>).
        ///
        /// <para>Different from <see cref="UnavailableReason"/> on purpose: that is a row the host
        /// cannot offer at all, this is one whose current answer is worth seeing and worth leaving
        /// alone. A read-only row states why in <see cref="UnavailableReason"/> all the same, since a
        /// control somebody cannot move has to say so.</para>
        /// </summary>
        public bool ReadOnly { get; set; }
    }

    /// <summary>
    /// One group box on the settings page: a handful of settings that come from the same file or the
    /// same unit, and can be unavailable together.
    /// </summary>
    public class PackageSettingGroup
    {
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// The note under the title, naming the file or the unit this group is a view of. It is not
        /// decoration: a page that changes <c>/etc/pacman.conf</c> without saying so is asking to be
        /// mistaken for a preference of VirtDeck's own.
        /// </summary>
        public string Hint { get; set; } = string.Empty;

        /// <summary>
        /// Why this whole group cannot be used on this host, or empty. Absent tooling is a stated
        /// answer: the group is drawn, greyed, with this sentence, rather than left out.
        /// </summary>
        public string UnavailableReason { get; set; } = string.Empty;

        /// <summary>
        /// The package that would make this group work, or empty. Present only alongside
        /// <see cref="UnavailableReason"/>, and it is what the group's one Install button installs.
        /// A constant inside the manager class and never user text, which is what keeps this away
        /// from being an install-anything box.
        /// </summary>
        public string MissingPackage { get; set; } = string.Empty;

        /// <summary>
        /// May be empty while <see cref="UnavailableReason"/> is set, which is pacman's automatic
        /// updates group: there is nothing to draw and the sentence is the whole content.
        /// </summary>
        public List<PackageSetting> Settings { get; set; } = new();
    }

    /// <summary>
    /// A file the save may rewrite, with the digest it had when the page read it.
    ///
    /// <para>The pair is what makes a save refuse rather than overwrite: somebody editing
    /// <c>/etc/pacman.conf</c> in this app's own Terminal module while the Settings tab sat open is
    /// not hypothetical. Same guard <c>CronService</c> puts in front of a crontab install, and it
    /// rides in the same round trip as the write for the same reason.</para>
    /// </summary>
    /// <param name="Path">The absolute path on the host.</param>
    /// <param name="Contents">
    /// Exactly the bytes the read brought back, or empty where there is no such file. The digest is
    /// taken from this rather than passed in beside it, so there is no way to hand the guard a digest
    /// of one thing and the page a copy of another.
    /// </param>
    public sealed record FileDigest(string Path, string Contents);

    /// <summary>
    /// One read of the host manager's settings, in <see cref="UpdateCatalog"/>'s shape and for the
    /// same reason: the page has to be able to draw every state this can be in, so "this manager has
    /// no settings" and "the read failed" are values rather than exceptions.
    /// </summary>
    public class PackageSettingCatalog
    {
        /// <summary>"apt", "dnf", "pacman", or empty where nothing VirtDeck knows is installed.</summary>
        public string ManagerId { get; set; } = string.Empty;

        /// <summary>
        /// What the page calls the tool in a sentence, which is why it is here and not derived: the
        /// failure line reads "apt could not read its settings", and an id cannot write that.
        /// </summary>
        public string ManagerName { get; set; } = string.Empty;

        /// <summary>There is a manager to talk to at all.</summary>
        public bool Available => ManagerId.Length > 0;

        public List<PackageSettingGroup> Groups { get; set; } = new();

        /// <summary>
        /// Every file the read brought back that a save might write, by path, holding exactly the bytes
        /// it had at the time. An empty value is a file that is not there, which is a real answer and
        /// the one VirtDeck's own apt drop-in starts from.
        ///
        /// <para>Carried on the catalog rather than held in the module, so the page hands back what it
        /// was given and neither the conflict guard nor a save that has to know what it wrote last time
        /// can drift from the read it belongs to.</para>
        /// </summary>
        public Dictionary<string, string> Files { get; set; } = new();

        /// <summary>
        /// What <see cref="Files"/> says about these paths, as the conflict guard wants it. A path the
        /// read never mentioned is skipped rather than guessed at as absent, because "I did not look"
        /// and "it is not there" would otherwise become the same claim and the second one is a licence
        /// to overwrite.
        /// </summary>
        public IReadOnlyList<FileDigest> DigestsFor(params string[] paths)
        {
            var digests = new List<FileDigest>();
            foreach (var path in paths)
                if (Files.TryGetValue(path, out var body))
                    digests.Add(new FileDigest(path, body));
            return digests;
        }

        /// <summary>
        /// The read ran, whatever it found. <b>This is what buys the third state</b>, exactly as
        /// <see cref="UpdateCatalog.Read"/> does: without it an empty page would read as "this
        /// manager has nothing to configure" on a host where the query failed.
        /// </summary>
        public bool Read { get; set; }

        /// <summary>
        /// Why the read failed, in the host's own words, or empty. Called a read failure rather than a
        /// list failure because nothing here is a listing: the other tab's name for this does not fit.
        /// </summary>
        public string ReadFailure { get; set; } = string.Empty;

        /// <summary>There is something to draw.</summary>
        public bool Any => Groups.Count > 0;
    }

    /// <summary>
    /// One setting the user moved. <paramref name="Value"/> is the wire spelling, so a change can be
    /// handed straight to the manager that produced the setting.
    ///
    /// <para><b>A value of null means take the key out, and that form is not a nicety.</b> Without it
    /// VirtDeck's apt drop-in would accrete every key anybody ever looked at and go on masking
    /// <c>20auto-upgrades</c> and <c>50unattended-upgrades</c> for ever, and a dnf key put back to its
    /// tool's own default would stay written as though somebody had chosen it. Removing is what makes
    /// "deleting our file is the whole undo" true from inside the app as well as from a terminal.</para>
    /// </summary>
    public sealed record PackageSettingChange(string Key, string? Value)
    {
        /// <summary>The key is being taken out rather than given a value.</summary>
        public bool Remove => Value is null;

        /// <summary>The value, or empty for a removal, for a caller that only wants text.</summary>
        public string Text => Value ?? string.Empty;
    }
}
