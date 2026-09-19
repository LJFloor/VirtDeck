using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Network;

/// <summary>
/// One route in an interface's details window. Rebuilt rather than merged: nothing polls this
/// table, so no refresh arrives unasked to drop a selection, and it has none to drop.
/// </summary>
public sealed class RouteRow
{
    public RouteRow(RouteEntry route, int index)
    {
        Route = route;
        Index = index;
    }

    public RouteEntry Route { get; }

    /// <summary>Where ip listed it, which is the table's own order: IPv4 first, each family as the kernel keeps it.</summary>
    public int Index { get; }

    public string Destination => Route.Destination;
    public string Gateway => Route.Gateway;
    public string Protocol => Route.Protocol;
    public string MetricText => Route.Metric?.ToString() ?? "";
    public string Scope => Route.Scope;
    public string Source => Route.Source;
}

/// <summary>One label and value on the General page, the disk details window's <c>DiskFact</c>.</summary>
public sealed record InterfaceFact(string Label, string Value, string Tip)
{
    public InterfaceFact(string label, string value) : this(label, value, value) { }
}
