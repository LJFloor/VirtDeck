using Avalonia.Controls;
using Avalonia.Input;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// What ZFS says about the pool: the state, its own <c>status:</c>, <c>action:</c>, <c>scan:</c>
/// and <c>errors:</c> paragraphs, and the one command that acts on any of it.
///
/// <para><b>Nothing here paraphrases the host.</b> The four sections are printed as ZFS wrote them,
/// because a scrub's progress figures and the sentence naming which device to replace are better
/// words than this app would find, and they change between OpenZFS releases. The parsing worked out
/// which section a line belonged to and nothing else.</para>
/// </summary>
public partial class PoolStatusTab : UserControl, IPoolTab, IPoolScrubRequest
{
    private PoolView? _view;

    public event Action<bool>? ScrubRequested;

    public PoolStatusTab()
    {
        InitializeComponent();

        ScrubButton.Click += (_, _) =>
        {
            if (_view is { } view) ScrubRequested?.Invoke(!view.ScanRunning);
        };

        SeeLink.PointerPressed += OnSeePressed;
    }

    public void Show(PoolView view)
    {
        _view = view;

        TitleName.Text = view.Pool.Name;
        TitleDot.Fill = view.StateBrush;
        TitleState.Text = view.Verdict;

        var status = view.Status;

        Section(StatusBlock, StatusText, status.StatusText);
        Section(ActionBlock, ActionText, status.ActionText);
        Section(ScanBlock, ScanText, status.ScanText);
        Section(ErrorsBlock, ErrorsText, status.ErrorsText);

        SeeBlock.IsVisible = status.SeeUrl.Length > 0;
        SeeLink.Text = status.SeeUrl;

        // Four empty states, because they are four different answers and only the last is a pool
        // with nothing to report.
        var anything = StatusBlock.IsVisible || ActionBlock.IsVisible ||
                       ScanBlock.IsVisible || ErrorsBlock.IsVisible;

        EmptyText.IsVisible = !anything;
        EmptyText.Text =
            !view.Probed ? "Reading the pool's status..."
            : status.Failure.Length > 0 ? status.Failure
            : "ZFS reported nothing about this pool beyond its state.";

        // The button reads the pool rather than what was last clicked, which is the client never
        // leading the host: a scrub started from a terminal flips this label on the next refresh.
        var running = view.ScanRunning;
        ScrubButton.Content = running ? "Stop scrub" : "Start scrub";
        ScrubButton.IsEnabled = view.Probed && status.Usable;
        ScrubButton.Tag = !view.Probed || !status.Usable
            ? "The pool's status could not be read, so there is nothing to scrub from here."
            : running
                ? "Stop the scrub that is running. Nothing already repaired is undone."
                : "Read every block on the pool and repair what redundancy can. It writes nothing " +
                  "and can be stopped at any time.";

        ScrubNote.Text = running
            ? ""
            : "A scrub is read-only and takes hours on a large pool. It is how a silently corrupt " +
              "block gets found before something needs to read it.";
    }

    private static void Section(Control block, TextBlock into, string text)
    {
        block.IsVisible = text.Length > 0;
        into.Text = text;
    }

    /// <summary>
    /// Opening the message-catalogue URL, which <b>fails soft</b>: a session on a remote host may
    /// have no browser at all, and the whole URL is on screen to be read.
    /// </summary>
    private void OnSeePressed(object? sender, PointerPressedEventArgs e)
    {
        if (SeeLink.Text is not { Length: > 0 } url) return;

        try { TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(url)); }
        catch { /* no browser, and the URL is on screen */ }
    }
}
