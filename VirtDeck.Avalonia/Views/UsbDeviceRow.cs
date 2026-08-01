using Avalonia.Media;
using SpiceClient.Usb;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One row of the USB picker. The list is rebuilt wholesale on every refresh; device identity
/// lives in <see cref="UsbDeviceInfo.Key"/>, not in the row, so nothing here needs to be
/// observable.
/// </summary>
public sealed class UsbDeviceRow
{
    private static readonly IBrush RedirectedBrush = Brush.Parse("#4a90d9");

    public UsbDeviceInfo Device { get; }

    public UsbDeviceRow(UsbDeviceInfo device) => Device = device;

    public string Name => Device.Description;
    public string Status => Device.IsRedirected ? "Redirected" : "";
    public IBrush StatusBrush => RedirectedBrush;
}
