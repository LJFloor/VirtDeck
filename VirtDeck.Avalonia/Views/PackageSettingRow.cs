using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views
{
    /// <summary>
    /// One row of the Software updates Settings page, as the markup binds to it.
    ///
    /// <para><b>A row per kind with one template each, rather than a control tree built in code.</b>
    /// Every form in this app is markup over named controls, and the only dynamic content anywhere is
    /// a row type plus a <c>DataTemplate</c> (the sixteen tables, <c>CheckRow</c> on the answer-file
    /// pages). Building <c>CheckBox</c>es in a loop would be the first of its kind here and would put
    /// the layout, the theming and the tooltip workaround in C# where sixteen other pages keep them in
    /// XAML. Avalonia picks the template off <c>DataType</c>, so a subclass per
    /// <see cref="SettingKind"/> is all the dispatch this needs, and the page still names no package
    /// manager: what a row is came out of the catalog that manager built.</para>
    ///
    /// <para><b>Two-way binding is right here, and that is worth saying because the app forbids it a
    /// row away.</b> A table row's tick is driven by <c>Click</c> rather than by its bound value,
    /// since a poll pushing a value in would generate commands; nothing polls this page and moving a
    /// control here issues nothing. Save is what talks to the host, which is also what makes
    /// <see cref="IsDirty"/> meaningful.</para>
    /// </summary>
    public abstract class PackageSettingRow : INotifyPropertyChanged
    {
        private readonly string _original;

        protected PackageSettingRow(PackageSetting setting)
        {
            Setting = setting;
            _original = setting.Value;
        }

        /// <summary>What the manager said this row is. The key travels back on a change.</summary>
        public PackageSetting Setting { get; }

        public string Key => Setting.Key;
        public string Label => Setting.Label;

        /// <summary>
        /// The tooltip: what the setting is for, and where it cannot be moved, why. Both, because a
        /// disabled row still has to explain what it would have done.
        /// </summary>
        public string Hint =>
            Setting.UnavailableReason.Length == 0
                ? Setting.Description
                : Setting.Description.Length == 0
                    ? Setting.UnavailableReason
                    : Setting.Description + "\n\n" + Setting.UnavailableReason;

        /// <summary>The unit beside a number, or empty. Drawn as its own cell, so an empty one collapses.</summary>
        public string Unit => Setting.Unit;

        /// <summary>Whether the control accepts input at all.</summary>
        public bool IsEditable => !Setting.ReadOnly;

        /// <summary>
        /// The value in the host's own spelling, which is the only spelling this class deals in: a
        /// subclass renders it for its control and parses it back, and nothing above here ever sees a
        /// display string.
        /// </summary>
        public abstract string Value { get; }

        /// <summary>Whether this row differs from what the host said, which is what enables Save.</summary>
        public bool IsDirty => !string.Equals(Value, _original, StringComparison.Ordinal);

        /// <summary>The change to send, or null where nothing moved.</summary>
        public PackageSettingChange? Change =>
            IsDirty ? new PackageSettingChange(Key, Value) : null;

        /// <summary>
        /// Builds the row a setting wants. The one place a <see cref="SettingKind"/> becomes a type,
        /// so the page never switches on a kind again.
        /// </summary>
        public static PackageSettingRow For(PackageSetting setting) => setting.Kind switch
        {
            SettingKind.Toggle => new ToggleSettingRow(setting),
            SettingKind.Number => new NumberSettingRow(setting),
            SettingKind.Choice => new ChoiceSettingRow(setting),
            SettingKind.List => new ListSettingRow(setting),
            _ => new TextSettingRow(setting),
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            Raise(nameof(Value));
            Raise(nameof(IsDirty));
            return true;
        }

        protected void Raise(string? name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A <c>CheckBox</c>. The host's spelling is "1" for on and empty for off.</summary>
    public sealed class ToggleSettingRow : PackageSettingRow
    {
        private bool _on;

        public ToggleSettingRow(PackageSetting setting) : base(setting) =>
            _on = setting.Value.Length > 0;

        public bool IsOn
        {
            get => _on;
            set => Set(ref _on, value);
        }

        public override string Value => _on ? "1" : string.Empty;

    }

    /// <summary>
    /// A <c>NumericUpDown</c>, which deals in <c>decimal?</c>, so this is where that becomes an
    /// integer again. <see cref="CultureInfo.InvariantCulture"/> throughout: the value is going into a
    /// config file, where a comma for a decimal point would be a different number or none at all.
    /// </summary>
    public sealed class NumberSettingRow : PackageSettingRow
    {
        private decimal _number;

        public NumberSettingRow(PackageSetting setting) : base(setting) =>
            _number = Parse(setting.Value, setting.Min);

        public decimal Number
        {
            get => _number;
            set => Set(ref _number, value);
        }

        public decimal Minimum => Setting.Min;
        public decimal Maximum => Setting.Max == int.MaxValue ? 999999 : Setting.Max;

        public override string Value =>
            ((int)_number).ToString(CultureInfo.InvariantCulture);

        private static decimal Parse(string text, int fallback) =>
            int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : fallback;
    }

    /// <summary>
    /// A <c>ComboBox</c>. It binds to the choices and to the selected one rather than to an index, so
    /// nothing here depends on the order the manager listed them in.
    /// </summary>
    public sealed class ChoiceSettingRow : PackageSettingRow
    {
        private SettingChoice _selected;

        public ChoiceSettingRow(PackageSetting setting) : base(setting) =>
            _selected = Find(setting.Value);

        public IReadOnlyList<SettingChoice> Choices => Setting.Choices;

        public SettingChoice Selected
        {
            get => _selected;
            set => Set(ref _selected, value ?? _selected);
        }

        public override string Value => _selected.Value;

        // A value the manager did not offer as a choice cannot happen: the manager turns such a
        // setting into a read-only text row before it ever gets here. The fallback is still the first
        // choice rather than null, because a ComboBox bound to null draws blank and reads as a value.
        private SettingChoice Find(string value) =>
            Setting.Choices.FirstOrDefault(c => c.Value == value)
            ?? Setting.Choices.FirstOrDefault()
            ?? new SettingChoice(value, value);
    }

    /// <summary>A <c>TextBox</c>, or a plain label where the row is read-only.</summary>
    public sealed class TextSettingRow : PackageSettingRow
    {
        private string _text;

        public TextSettingRow(PackageSetting setting) : base(setting) => _text = setting.Value;

        public string Text
        {
            get => _text;
            set => Set(ref _text, value ?? string.Empty);
        }

        public override string Value => _text;
    }

    /// <summary>
    /// A list of values behind an Edit button, edited in <c>ListSettingWindow</c>.
    ///
    /// <para><b>The only row whose control does not hold the value.</b> The other four are a tick, a
    /// spinner, a dropdown and a box, and each is the value; this one is a button, and what it opens
    /// hands an answer back or does not. So the row owns the entries and the window edits a copy,
    /// which is also what makes Cancel mean something without the page having to remember a
    /// before.</para>
    ///
    /// <para>The wire spelling is the entries joined by a newline, as
    /// <see cref="SettingKind.List"/> defines it, so <see cref="PackageSettingRow.IsDirty"/> and the
    /// change that travels on it work exactly as they do for every other row: order counts, because
    /// the file keeps it.</para>
    /// </summary>
    public sealed class ListSettingRow : PackageSettingRow
    {
        private List<string> _items;

        public ListSettingRow(PackageSetting setting) : base(setting) => _items = Split(setting.Value);

        /// <summary>The entries, as the editor window's starting point and never edited in place.</summary>
        public IReadOnlyList<string> Items => _items;

        /// <summary>What the editor window puts at the top of itself.</summary>
        public string EditorNote => Setting.EditorNote;

        /// <summary>The greyed example in an empty row of the editor.</summary>
        public string ItemPlaceholder => Setting.ItemPlaceholder;

        /// <summary>
        /// What the page shows beside the button: the entries, comma joined and trimmed by the
        /// control. An empty list says so in words rather than leaving the cell blank, because a blank
        /// there reads as "not read yet" and this one is a real and consequential answer.
        /// </summary>
        public string Summary =>
            _items.Count > 0 ? string.Join(", ", _items) :
            Setting.EmptySummary.Length > 0 ? Setting.EmptySummary : "Nothing configured";

        public bool IsEmpty => _items.Count == 0;

        /// <summary>What the editor window accepted. Raises the same three as every other setter.</summary>
        public void Replace(IEnumerable<string> items)
        {
            var next = items.Select(i => i.Trim()).Where(i => i.Length > 0).ToList();
            if (next.SequenceEqual(_items, StringComparer.Ordinal)) return;

            _items = next;
            Raise(nameof(Items));
            Raise(nameof(Summary));
            Raise(nameof(IsEmpty));
            Raise(nameof(Value));
            Raise(nameof(IsDirty));
        }

        public override string Value => string.Join("\n", _items);

        private static List<string> Split(string value) =>
            value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                 .Select(v => v.Trim())
                 .Where(v => v.Length > 0)
                 .ToList();
    }

    /// <summary>
    /// One group box, and the rows in it. It is a row type of its own for the same reason the settings
    /// are: the page is an <c>ItemsControl</c> over these, so adding a group is a line in a manager
    /// class rather than a block of markup.
    /// </summary>
    public sealed class PackageSettingGroupRow
    {
        public PackageSettingGroupRow(PackageSettingGroup group)
        {
            Group = group;
            Rows = group.Settings.Select(PackageSettingRow.For).ToList();
        }

        public PackageSettingGroup Group { get; }
        public IReadOnlyList<PackageSettingRow> Rows { get; }

        public string Title => Group.Title;
        public string Hint => Group.Hint;

        /// <summary>
        /// Only where the group has something to describe. A group the host cannot support states its
        /// reason, and a hint about what the group would have been is that same fact a second time.
        /// </summary>
        public bool HasHint => Group.Hint.Length > 0 && !IsUnavailable;

        /// <summary>
        /// Why nothing in this group can be used, or empty. Drawn as the group's own sentence, which
        /// on an Arch host is the whole of the Automatic updates group: there is nothing to grey out,
        /// the refusal is the content.
        /// </summary>
        public string UnavailableReason => Group.UnavailableReason;
        public bool IsUnavailable => Group.UnavailableReason.Length > 0;

        /// <summary>The rows accept input. False greys the whole subtree in one binding.</summary>
        public bool IsEditable => !IsUnavailable;

        /// <summary>The package that would make this group work, and the button's own label.</summary>
        public string MissingPackage => Group.MissingPackage;
        public bool CanInstall => Group.MissingPackage.Length > 0;
        public string InstallLabel => $"Install {Group.MissingPackage}";
    }
}
