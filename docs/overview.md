# What this is

An app to manage a remote Linux host over SSH. Its first and largest subject is libvirt/KVM VMs, with a **native C# SPICE console** (no WebView2, no spice-html5, no WebSocket bridge; raw TCP to the SSH-forwarded SPICE port); Docker containers are the second. One SSH connection serves all of it, and the main window is a side menu of **modules** over that connection.

**Cross-platform status:** done. Engine, services, management UI, guest audio and USB redirection run on Linux and Windows from the single Avalonia front-end. The WinForms app this was ported from is deleted (it is in git history if a detail ever needs checking).

It is a fresh rewrite of the older `..\VmManager` app, which rendered SPICE via spice-html5 in WebView2 and broke with "Protocol Error" (the WebSocket-to-TCP bridge mangled SPICE's binary framing). The SPICE protocol was ported field-for-field from the bundled spice-html5 source at `..\VmManager\VmManager\WebContent\src\*.js`; **that JS is the authoritative wire-format reference.**

**Flow:** `Views/Hosts/HostManagerWindow` (the saved hosts, and the only way in) -> SSH connect -> `MainWindow`, holding a `ShellView` (module side menu) -> Virtual machines -> double-click VM -> `ConsoleWindow` (native SPICE). One host is live at a time; the status bar's host cell switches between the saved ones, which is a **replace** of the shell rather than a re-attach (see "Saved hosts").

