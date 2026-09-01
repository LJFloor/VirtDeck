using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// One row of the Health tab's ATA attribute table.
///
/// <para>Immutable and rebuilt rather than merged and updated, which is the Software updates History
/// tab's answer and holds here for the same reason: nothing polls this window, so no refresh ever
/// arrives unasked to drop a selection, and an attribute table that has already been read cannot
/// change underneath its own row.</para>
///
/// <para><b>The colour is two booleans and not a brush</b>, unlike every other row view model in
/// this app. A brush would have to be non-null on every row, since a null one bound to
/// <c>Foreground</c> suppresses the inherited value rather than falling back to it, and the only
/// non-null answer for an ordinary attribute is whatever the theme would have used anyway. So the
/// two exceptional states are classes the template switches on and the ordinary row is left alone,
/// which is also the only version of this that reads correctly on both faces.</para>
/// </summary>
public sealed class SmartAttributeRow
{
    private readonly SmartAttribute _attribute;

    public SmartAttributeRow(SmartAttribute attribute) => _attribute = attribute;

    /// <summary>
    /// Decimal and hex, because the two halves of the world disagree about which one an attribute is
    /// called by: smartctl prints 5, every drive datasheet and CrystalDiskInfo print 05.
    /// </summary>
    public string IdText => $"{_attribute.Id} ({_attribute.Id:X2})";

    /// <summary>
    /// smartctl's own name for the attribute, underscores and all. Kept exactly as the tool wrote
    /// it, which is this app's rule for anything the host said: prettifying
    /// <c>Reallocated_Sector_Ct</c> would make it harder to search for, not easier to read.
    /// </summary>
    public string Name => _attribute.Name;

    public string CurrentText => _attribute.Value?.ToString() ?? "";
    public string WorstText => _attribute.Worst?.ToString() ?? "";
    public string ThresholdText => _attribute.Threshold?.ToString() ?? "";

    // The values the cells above were rendered from, for the sort. Every table in this app orders
    // on the figure and never on the text in the cell, which here is the difference between 99,
    // 100, 253 and "100", "253", "99"; the nullables sort straight, so an attribute the drive
    // stated nothing for goes to one end rather than being given an invented number among the real
    // ones.
    public int Id => _attribute.Id;
    public int? Current => _attribute.Value;
    public int? Worst => _attribute.Worst;
    public int? Threshold => _attribute.Threshold;
    public long? RawValue => _attribute.Raw;

    /// <summary>
    /// Worst first, so one ascending click on Status puts the attributes worth reading at the top.
    /// The same argument that puts a failing disk at the top of the storage table.
    /// </summary>
    public int StatusOrder => StatusOrderOf(_attribute);

    /// <inheritdoc cref="StatusOrder"/>
    /// <remarks>
    /// Static so the Health tab's sort can key on an attribute without building a row per element
    /// to ask it, which is what <c>OrderBy</c> would otherwise do once for every attribute on the
    /// drive.
    /// </remarks>
    public static int StatusOrderOf(SmartAttribute a) =>
        a.FailingNow && a.PreFailure ? 0
        : a.FailingNow ? 1
        : a.WhenFailed.Length > 0 ? 2
        : a.AtThreshold ? 3
        : 4;

    /// <summary>
    /// smartctl's own rendering of the raw value where it has one, and the bare number otherwise.
    /// The string is the useful half far more often than not: attribute 194 reads
    /// <c>31 (Min/Max 24/45)</c> and attribute 9 <c>14523h+21m+43.480s</c>, and the number alone
    /// throws away whatever the vendor packed into the other bytes.
    /// </summary>
    public string RawText =>
        _attribute.RawString.Length > 0 ? _attribute.RawString : _attribute.Raw?.ToString() ?? "";

    /// <summary>
    /// What the drive says about this attribute now. "OK" is drawn rather than left blank, because
    /// this is the column somebody scans down and a blank cell would read as missing data rather
    /// than as good news.
    /// </summary>
    public string StatusText =>
        _attribute.FailingNow ? "Failing now"
        : _attribute.WhenFailed.Length > 0 ? "Failed before"
        : _attribute.AtThreshold ? "At threshold"
        : "OK";

    /// <summary>
    /// Failing <b>now</b>, and only a pre-fail attribute counts. That is udisks2's own definition
    /// and the same one the module-wide verdict uses: an old drive with a worn old-age counter is
    /// not a sick drive, and colouring it red here would contradict the green dot on its row.
    /// </summary>
    public bool IsFailing => _attribute.FailingNow && _attribute.PreFailure;

    /// <summary>
    /// Worth a second look but not a verdict: a past failure, an old-age attribute failing now, or
    /// one sitting on its threshold.
    /// </summary>
    public bool IsWarning =>
        !IsFailing && (_attribute.WhenFailed.Length > 0 || _attribute.AtThreshold);

    /// <summary>
    /// The row tooltip, and the one place the pre-fail/old-age distinction is said. It earns a
    /// tooltip rather than a column because it is the same word on almost every row of a given
    /// drive, and because it is what the two colours above already mean.
    /// </summary>
    public string Tip
    {
        get
        {
            var kind = _attribute.PreFailure
                ? "A pre-fail attribute: the drive treats this one as a predictor of failure."
                : "An old-age attribute: it tracks wear rather than predicting failure.";

            var updated = _attribute.UpdatedOnline
                ? " Updated during normal operation."
                : " Updated only during offline self-tests.";

            var normalised =
                _attribute.Value is { } v && _attribute.Threshold is { } t && t > 0
                    ? $" Normalised to {v}, failing at {t}."
                    : " The drive states no threshold for it.";

            return kind + updated + normalised;
        }
    }
}

/// <summary>
/// One line of the Health tab's NVMe table.
///
/// <para>NVMe has no attribute table at all: the protocol defines one fixed SMART/Health Information
/// log instead. So the tab draws a name and a value here where an ATA drive gets seven columns,
/// rather than an ATA table with five of its columns empty, which is the absent-tooling rule applied
/// to a column: a column of blanks is not an answer.</para>
/// </summary>
public sealed record NvmeRow(string Name, string Value, string Tip)
{
    public NvmeRow(string name, string value) : this(name, value, "") { }
}

/// <summary>
/// One line of the Health tab's self-test log.
///
/// <para>Read and never started. Running a self test is a write to the drive, takes minutes to hours
/// and cannot be called back, which puts it outside what this window does; what it can usefully say
/// is whether the drive has ever been told to check itself and what happened when it was.</para>
/// </summary>
public sealed class SelfTestRow
{
    private readonly SelfTestEntry _entry;

    public SelfTestRow(SelfTestEntry entry) => _entry = entry;

    public string NumText => _entry.Num.ToString();
    public string TypeText => _entry.Type;
    public string StatusText => _entry.Status;

    /// <summary>
    /// When the test ran, expressed as the drive's own power-on hours at the time, which is the only
    /// clock a drive keeps. It is rendered as an age rather than a five-digit hour count for the
    /// reason the storage table's Powered on column is.
    /// </summary>
    public string WhenText => _entry.LifetimeHours is { } h ? StorageRow.Age(h) : "";

    public string WhenTip => _entry.LifetimeHours is { } h
        ? $"At {h:N0} hours powered on" + (_entry.LbaFirstError is { } lba
            ? $", stopping at LBA {lba:N0}"
            : "")
        : "";

    public bool Failed => _entry.Failed;
}
