using Avalonia.Media;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The three colours a state dot is drawn in, in one place, so a dot means the same thing wherever
/// it appears: green usable, amber transient, grey not going anywhere.
///
/// <para>Deliberately outside the theme dictionaries, for the reason <c>JbIconAdd</c> and
/// <c>JbIconRemove</c> are: what these say must not change with the light or dark face. The one
/// state colour that is themed is the failed-unit red in the services list, because grey would
/// bury the most important row there and nothing here has that problem.</para>
///
/// <para>Hoisted out of <see cref="ContainerRow"/> when <see cref="DockerStackRow"/> needed the same
/// three. A stack gets a dot where a network does not, because a stack is a set of containers and
/// so it is doing something, where a network is a fact.</para>
/// </summary>
internal static class StateBrushes
{
    public static readonly IBrush Running = new SolidColorBrush(Color.FromRgb(0x2e, 0x9e, 0x4f));
    public static readonly IBrush Transient = new SolidColorBrush(Color.FromRgb(0xd6, 0x8f, 0x00));
    public static readonly IBrush Stopped = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
}
