# CLAUDE.md

Guidance for Claude Code when working in this repository.

VirtDeck manages a remote Linux host over SSH: libvirt/KVM VMs with a native C# SPICE console,
Docker containers, services, packages, accounts, storage, files and a terminal, all over one SSH
connection, from a single Avalonia front end that runs on Windows and Linux.

```bash
dotnet build VirtDeck.sln
```

A clean solution build is the no-regression gate. There are no tests;
`dotnet run --project VirtDeck.Avalonia` is the fastest smoke test. See
[docs/build-and-packaging.md](docs/build-and-packaging.md) for the projects, the release artifacts
and what CI does and does not do.

## Conventions

- All wire structs follow the spice-html5 source byte-for-byte; when changing protocol code, check the matching `*.js` in `..\VmManager\VmManager\WebContent\src`.
- Pixels are BGRA end-to-end; do NOT copy spice-html5's BGRA-to-RGBA canvas swap.
- UI updates from channel threads go through `Dispatcher.UIThread`; framebuffer access is under `SpiceFramebuffer.SyncRoot`.
- Do not add a local `FontSize`/`Height` in a view to line controls up; fix the baseline in `Styles/JetBrainsClassic.axaml`.
- No em dashes or en dashes anywhere in the repo (the vendored `third-party/` tree is exempt).
- `third-party/unattend-generator` is vendored MIT source and is not ours to edit; read its `VENDORED.md` first.

## Documentation index

The rest of this guidance lives in `docs/`. Read the cross-cutting three before changing anything;
the rest is one file per subject. The files quote each other by **section name** (for example, "see
'Shared idioms'" or "see 'One listing, two pages'"), so a name in the prose is a heading somewhere
in this index.

### Start here

| File | What is in it |
|---|---|
| [overview.md](docs/overview.md) | What the app is, the flow from the host manager to a console, and the older app it was rewritten from. |
| [build-and-packaging.md](docs/build-and-packaging.md) | The four projects, the two release artifacts, and why CI uploads nothing. |
| [shared-idioms.md](docs/shared-idioms.md) | The rules that recur everywhere: tab walks, one round trip with tagged records, never interpolating user text into a shell, absent tooling as a stated answer, the refresh policy, merged rather than rebuilt rows, sorting and filtering, auto-fill. |

### Across the app

| File | What is in it |
|---|---|
| [architecture.md](docs/architecture.md) | SpiceClient (protocol, imaging, audio, interop), VirtDeck.Core (services, SSH auth, settings, secrets), VirtDeck.Avalonia (controls, views, input), and the JetBrains Classic style dictionary that is the app's single source of visual scale. |
| [modules.md](docs/modules.md) | The shell rather than a screen: `IModule`, `Attach`, the two status bar slots plus `StatusWidget`, `BusyReason`, and which modules a host gets from one `command -v` probe. |
| [saved-hosts.md](docs/saved-hosts.md) | Remembering passwords in the OS secret store, the saved host list, switching host by replacing the shell, and `HostManagerWindow`, the app's only way in. |
| [file-icons.md](docs/file-icons.md) | The host desktop's icon per file type: SHGetFileInfoW on Windows, shared-mime-info plus the icon theme on Linux. |

### Virtual machines and the SPICE console

| File | What is in it |
|---|---|
| [virtual-machines.md](docs/virtual-machines.md) | Host capability probes, ISO identification and guest device defaults, Export VM, clipboard sharing both ways, the one-cursor rule, image compression and QUIC, and removable ISO/floppy media over NBD. |
| [usb-redirection.md](docs/usb-redirection.md) | The native usbredir stack, why a channel that cannot be driven is never linked, mass storage preparation per platform, and host provisioning. |
| [unattend.md](docs/unattend.md) | The answer file window over the vendored unattend-generator: the page vocabulary, the code boxes, the answer disc, the flat model and its two typing rules, and validation. |

### Modules

| File | What is in it |
|---|---|
| [dashboard.md](docs/dashboard.md) | The host itself over live CPU, memory, network, disk IO and GPU graphs, fed by one long-lived sampling tail rather than a poll. |
| [logs.md](docs/logs.md) | The host's journal: the three dropdowns that are a query rather than a filter, cursor paging, the live tail and Pause, and why every read is elevated. |
| [containers.md](docs/containers.md) | The four tabs over one docker host, `DockerService`, the listing and the event tail, and the Docker Hub account cell in the status bar. |
| [containers-images.md](docs/containers-images.md) | Listing, pulling, tagging, removing, pruning, and moving an image to and from this PC. |
| [containers-networks.md](docs/containers-networks.md) | Listing, creating, removing and pruning networks, and attaching or detaching a container. |
| [containers-stacks.md](docs/containers-stacks.md) | Docker compose projects: label discovery, the stacks root on the host, the editor, and deploy/down/delete. |
| [containers-editing.md](docs/containers-editing.md) | One window for create and edit, why editing recreates, the image half of the read-back, the six newer pages, and GPU passthrough. |
| [terminal-emulator.md](docs/terminal-emulator.md) | The three surfaces drawn by `TerminalControl`: reading a container's log, running a command in one, the PTY session, the emulator itself and mouse reporting. |
| [services.md](docs/services.md) | systemd units in both scopes, the three read scripts and why they are three, the journal tail, and the commands. |
| [software-updates.md](docs/software-updates.md) | apt, dnf and pacman behind one interface, the progress percentage and when Cancel goes away, reboot and history, and the listing the Dashboard shares. |
| [user-accounts.md](docs/user-accounts.md) | Users and groups, the name suggestion, the password path over stdin, and deleting. |
| [storage.md](docs/storage.md) | The host's disks from one `lsblk -J`, and what SMART says about each one. |
| [storage-disk-details.md](docs/storage-disk-details.md) | One disk in two pages: what it is and what is stacked on it, the full SMART reading, and mounting, unmounting and unlocking what is on it. |
| [storage-zfs.md](docs/storage-zfs.md) | The host's pools: listing, status, topology, create, scrub, import, export and destroy. |
| [storage-zfs-datasets.md](docs/storage-zfs-datasets.md) | What is inside a pool, as a tree under it: filesystems and volumes, their properties, and create, edit, rename and destroy. |
| [file-explorer.md](docs/file-explorer.md) | Browsing as the login user, the one-shot root retry, cut/copy/paste, deleting, transfers in both directions, and dragging. |
| [terminal.md](docs/terminal.md) | One shell on the host as the logged-in user, and how the keyboard is handled across the three terminal surfaces. |
