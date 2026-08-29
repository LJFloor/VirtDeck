using Avalonia.Controls;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>Which container to attach or detach, and at which address if it is being attached.</summary>
public sealed record NetworkAttachRequest(string ContainerId, string ContainerName, string Ip);

/// <summary>
/// Attaches a container to a network or detaches one from it.
///
/// <para>One class with a mode rather than two, because the two differ only in the title, the verb
/// on the primary, which containers the picker holds and whether one row is on screen. Two classes
/// would be the same ComboBox written twice.</para>
///
/// <para>It is deliberately dumb: the caller has the network row already and computes both
/// candidate lists from it, so this asks the host nothing, exactly as
/// <see cref="ImageTagDialog"/> takes its source string and asks nothing.</para>
/// </summary>
public partial class NetworkAttachDialog : Window
{
    private readonly IReadOnlyList<DockerNetworkMember> _choices;

    /// <summary>What to do, or null while the dialog has not been accepted.</summary>
    public NetworkAttachRequest? Result { get; private set; }

    /// <summary>Design-time only.</summary>
    public NetworkAttachDialog() : this(true, "bridge", new List<DockerNetworkMember>(), true) { }

    public NetworkAttachDialog(bool connect, string network,
                               IReadOnlyList<DockerNetworkMember> choices, bool canTakeIp)
    {
        InitializeComponent();
        _choices = choices;

        Title = connect ? "Connect container" : "Disconnect container";
        AcceptButton.Content = connect ? "Connect" : "Disconnect";

        ContainerBox.ItemsSource = choices.Select(c => c.Name).ToList();
        if (choices.Count > 0) ContainerBox.SelectedIndex = 0;

        IpLabel.IsVisible = connect;
        IpHost.IsVisible = connect;

        if (connect && !canTakeIp)
        {
            // Docker: "user specified IP address is supported on user defined networks only". The
            // predicate is "not predefined" rather than "has a subnet", because a user-defined
            // bridge always gets one from the default pool and gating on that would grey the box
            // on exactly the networks where it works.
            IpBox.IsEnabled = false;
            ToolTip.SetTip(IpHost,
                $"Docker only accepts a fixed address on a network you created, not on {network}.");
        }

        NoteText.Text = connect
            ? $"Attaches the container to {network}. It takes effect at once on a running " +
              "container, and containers on the same user-defined network can reach each other by name."
            : $"Detaches the container from {network}. A stopped container can be detached too, " +
              "which is how one that will not start for want of a missing network is put right.";

        CancelButton.Click += (_, _) => Close(false);
        AcceptButton.Click += (_, _) => Accept();
        Opened += (_, _) => ContainerBox.Focus();
    }

    private void Accept()
    {
        var index = ContainerBox.SelectedIndex;

        if (index < 0 || index >= _choices.Count)
        {
            ErrorText.Text = "Pick a container.";
            ErrorText.IsVisible = true;
            ContainerBox.Focus();
            return;
        }

        var chosen = _choices[index];
        Result = new NetworkAttachRequest(chosen.Id, chosen.Name, (IpBox.Text ?? "").Trim());
        Close(true);
    }
}
