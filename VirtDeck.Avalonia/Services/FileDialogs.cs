using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace VirtDeck.Avalonia.Services;

/// <summary>
/// Local file pickers over Avalonia's <see cref="IStorageProvider"/> (the XDG portal on Linux,
/// the common item dialogs on Windows). The rest of the app still speaks the WinForms filter
/// string; <see cref="ParseFilter"/> is the one place that dialect is translated.
/// </summary>
public static class FileDialogs
{
    /// <summary>
    /// Opens one local file; null if cancelled. <paramref name="startDirectory"/> is a hint: a directory
    /// that has since been deleted or moved resolves to null and the picker falls back to its own
    /// default, which is why the lookup failing is not an error.
    /// </summary>
    public static async Task<string?> OpenFileAsync(Window owner, string title, string filter,
                                                    string? startDirectory = null)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = ParseFilter(filter),
            SuggestedStartLocation = await FolderOrNull(owner, startDirectory),
        });
        return LocalPathOf(files.FirstOrDefault());
    }

    private static async Task<IStorageFolder?> FolderOrNull(Window owner, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return await owner.StorageProvider.TryGetFolderFromPathAsync(path); }
        catch { return null; }
    }

    /// <summary>Chooses a save location; null if cancelled.</summary>
    public static async Task<string?> SaveFileAsync(Window owner, string title, string filter,
                                                    string? suggestedName = null,
                                                    string? defaultExtension = null)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = defaultExtension,
            FileTypeChoices = ParseFilter(filter),
        });
        return LocalPathOf(file);
    }

    /// <summary>
    /// Only local paths are usable; everything downstream (NBD streaming, tar writing) works on
    /// <see cref="FileStream"/>, not on portal handles. A non-file URI reads as "nothing picked".
    /// Shared with <see cref="DropFiles"/>, so a dropped item is filtered by the same rule.
    /// </summary>
    internal static string? LocalPathOf(IStorageItem? item)
    {
        var path = item?.TryGetLocalPath();
        return string.IsNullOrEmpty(path) ? null : path;
    }

    /// <summary>
    /// Converts a WinForms filter string ("ISO images (*.iso)|*.iso|All files (*.*)|*.*") into
    /// picker file types. "*.*"/"*" becomes the wildcard type, which the portal understands.
    /// </summary>
    public static List<FilePickerFileType> ParseFilter(string? filter)
    {
        var types = new List<FilePickerFileType>();
        if (string.IsNullOrWhiteSpace(filter)) return types;

        var parts = filter.Split('|');
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            var patterns = parts[i + 1]
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
            if (patterns.Length == 0) continue;

            types.Add(patterns.Any(p => p is "*.*" or "*")
                ? FilePickerFileTypes.All
                : new FilePickerFileType(parts[i]) { Patterns = patterns });
        }
        return types;
    }
}
