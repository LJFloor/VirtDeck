using Avalonia.Controls;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// Puts another reference on an image.
///
/// <para>The box is checked for the two things that are certainly wrong (empty, and whitespace
/// inside a reference) and nothing else. What names docker will accept is docker's rule, it changes
/// without us, and its refusal names the reference; a fuller pattern here would be a worse copy that
/// eventually rejects something valid.</para>
/// </summary>
public partial class ImageTagDialog : Window
{
    /// <summary>The new reference, or null while the dialog has not been accepted.</summary>
    public string? Target { get; private set; }

    /// <summary>Design-time only.</summary>
    public ImageTagDialog() : this("image:latest") { }

    public ImageTagDialog(string source)
    {
        InitializeComponent();

        SourceBox.Text = source;

        CancelButton.Click += (_, _) => Close(false);
        TagButton.Click += (_, _) => Accept();

        Opened += (_, _) => TargetBox.Focus();
    }

    private void Accept()
    {
        var target = TargetBox.Text?.Trim() ?? string.Empty;

        var problem = target.Length == 0 ? "Give the image a new name."
                    : target.Any(char.IsWhiteSpace) ? "An image name cannot contain spaces."
                    : null;

        if (problem is not null)
        {
            ErrorText.Text = problem;
            ErrorText.IsVisible = true;
            TargetBox.Focus();
            return;
        }

        Target = target;
        Close(true);
    }
}
