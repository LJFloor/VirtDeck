using Avalonia.Controls;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// Which image to pull, and nothing else.
///
/// <para>It deliberately does not run the pull. A pull is long, cancellable and chatty, and the
/// module already owns the two things that suits: the shell's status slot and the transfer strip
/// with its Cancel. A progress area grown here would be a second place in the app where "something
/// is running and you can stop it" is drawn, and it would be one the user cannot leave.</para>
///
/// <para>The reference is not validated against docker's grammar. The registry is the authority on
/// what it will serve, its refusal names the reference, and a client-side pattern would only ever be
/// a worse copy of a rule that changes without us.</para>
/// </summary>
public partial class PullImageDialog : Window
{
    /// <summary>What to pull, or null while the dialog has not been accepted.</summary>
    public string? Reference { get; private set; }

    public PullImageDialog()
    {
        InitializeComponent();

        CancelButton.Click += (_, _) => Close(false);
        PullButton.Click += (_, _) => Accept();

        Opened += (_, _) => ReferenceBox.Focus();
    }

    private void Accept()
    {
        var reference = ReferenceBox.Text?.Trim() ?? string.Empty;
        if (reference.Length == 0)
        {
            ErrorText.Text = "Name an image to pull.";
            ErrorText.IsVisible = true;
            ReferenceBox.Focus();
            return;
        }

        Reference = reference;
        Close(true);
    }
}
