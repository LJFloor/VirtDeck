namespace SpiceClient.Imaging;

/// <summary>
/// Anything that paints a <see cref="SpiceFramebuffer"/> and says when: the SPICE session, and the
/// Remote Control module's RFB session. It is what the Avalonia display control attaches to, so
/// the dirty-row pump, the render and the one-cursor rule are written once for both protocols.
///
/// Every event is raised on the source's own thread (a channel's read thread); subscribers
/// marshal. The framebuffer is replaced, never resized, on a resolution change, and whoever
/// swaps it in on the UI thread disposes the previous one, as <c>SpiceDisplay</c> always has.
/// </summary>
public interface IFramebufferSource
{
    /// <summary>The current surface, or null before the first one exists.</summary>
    SpiceFramebuffer? Framebuffer { get; }

    /// <summary>A new surface of this size replaced the previous one.</summary>
    event Action<int, int>? ResolutionChanged;

    /// <summary>Something was drawn; the dirty regions are on the framebuffer.</summary>
    event Action? FrameDirty;

    /// <summary>The far end's cursor took this shape.</summary>
    event Action<CursorShape>? CursorSet;

    /// <summary>The far end hid its cursor.</summary>
    event Action? CursorHidden;

    /// <summary>The far end's cursor went back to the system default.</summary>
    event Action? CursorReset;
}
