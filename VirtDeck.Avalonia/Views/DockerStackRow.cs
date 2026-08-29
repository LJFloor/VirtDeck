using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One row of the stacks list: a docker compose project, with the commands it can accept worked out
/// from what the host said about it.
///
/// <para>The merge key is the project <b>name</b>, not an id, because a compose project has no id:
/// its identity is the string compose stamped on every container it made. That is
/// <see cref="ImageRow"/>'s situation and it takes the same answer.</para>
///
/// <para>Unlike <see cref="DockerNetworkRow"/> this row does get a state dot, because a stack is a
/// set of containers and so it is doing something, where a network is a fact.</para>
/// </summary>
public sealed class DockerStackRow : INotifyPropertyChanged
{
    /// <summary>The compose project name, and the merge key.</summary>
    public string Name { get; }

    private bool _managed;

    /// <summary>
    /// Whether the compose file is one of VirtDeck's own, which is the single fact deciding whether
    /// this stack can be edited and deleted. Everything else works either way.
    /// </summary>
    public bool Managed { get => _managed; private set { if (Set(ref _managed, value)) Raise(nameof(Source)); } }

    /// <summary>
    /// "VirtDeck" or "External", the column that says at a glance which stacks this app wrote and
    /// which somebody brought up by hand. The word Portainer would use here is "limited"; this says
    /// where the file came from instead, because that is the actual difference and it is the thing
    /// that explains why the editor opens read-only.
    /// </summary>
    public string Source => _managed ? "VirtDeck" : "External";

    private string _configPath = "";

    /// <summary>The first compose file, which is the one the editor opens and the column shows.</summary>
    public string ConfigPath { get => _configPath; private set => Set(ref _configPath, value); }

    /// <summary>Every compose file, for the tooltip and for the editor's file picker.</summary>
    public IReadOnlyList<string> ConfigFiles { get; private set; } = new List<string>();

    /// <summary>What the Config file cell says on hover: the whole list where there is more than one, and the reason when it is gone.</summary>
    public string? ConfigTip
    {
        get
        {
            if (_configPath.Length == 0) return "Nothing on the host says where this stack's compose file is.";
            var listed = ConfigFiles.Count > 1 ? string.Join("\n", ConfigFiles) : _configPath;
            return _configPresent
                ? listed
                : listed + "\n\nThis file is no longer on the host, so the stack cannot be deployed " +
                           "or taken down until it is put back.";
        }
    }

    private bool _configPresent;
    public bool ConfigPresent { get => _configPresent; private set => Set(ref _configPresent, value); }

    private string _workingDir = "";
    public string WorkingDir { get => _workingDir; private set => Set(ref _workingDir, value); }

    private IReadOnlyList<DockerStackMember> _members = new List<DockerStackMember>();
    public IReadOnlyList<DockerStackMember> Members => _members;

    private bool _membersKnown;
    public bool MembersKnown => _membersKnown;

    /// <summary>Whether the host has the compose plugin, which three of the commands need and three do not.</summary>
    private bool _composeAvailable;

    private int RunningCount => _members.Count(m => m.State is "running" or "restarting");

    /// <summary>How many services this stack has containers for, which is what the Services column counts.</summary>
    public string ServiceCount => _members.Count == 0
        ? ""
        : _members.Select(m => m.Service).Distinct(StringComparer.Ordinal).Count().ToString();

    /// <summary>
    /// The stack as a whole, read off its containers.
    ///
    /// <para>"Not deployed" and "Stopped" are different answers and both are real: the first is a
    /// stack VirtDeck has a compose file for and nothing has been created from, the second is one
    /// whose containers exist and are all down. Only the first is what Deploy is for.</para>
    /// </summary>
    public string Status
    {
        get
        {
            if (!_membersKnown) return "";
            if (_members.Count == 0) return "Not deployed";
            var up = RunningCount;
            if (up == 0) return $"Stopped ({_members.Count})";
            return up == _members.Count ? $"Running {up}/{_members.Count}" : $"Partial {up}/{_members.Count}";
        }
    }

    /// <summary>
    /// Green only when every container is up. A stack half of which is running is amber and not
    /// green, because "the stack is running" is exactly the wrong thing to tell somebody whose
    /// database container died.
    /// </summary>
    public IBrush StateBrush
    {
        get
        {
            if (!_membersKnown || _members.Count == 0) return StateBrushes.Stopped;
            if (_members.Any(m => m.State is "restarting" or "paused" or "removing")) return StateBrushes.Transient;
            var up = RunningCount;
            if (up == 0) return StateBrushes.Stopped;
            return up == _members.Count ? StateBrushes.Running : StateBrushes.Transient;
        }
    }

    /// <summary>What the Status cell says on hover: the services and what each one is doing.</summary>
    public string? StatusTip
    {
        get
        {
            if (!_membersKnown) return "The host could not be asked what this stack is running.";
            if (_members.Count == 0)
                return "This stack has a compose file on the host and nothing created from it. Deploy brings it up.";
            return string.Join("\n", _members
                .OrderBy(m => m.Service, StringComparer.OrdinalIgnoreCase)
                .Select(m => $"{(m.Service.Length > 0 ? m.Service : m.Name)}: {m.State}"));
        }
    }

    // The command predicates. Every one is a pure function of what the host last said, so UpdateMenu
    // stays a pure function of the cached rows and never of a read flag in flight.

    /// <summary>Up needs the plugin and needs the file it reads to be there.</summary>
    public bool CanDeploy => _composeAvailable && _configPresent;

    /// <summary>Down needs the same, plus something to take down.</summary>
    public bool CanDown => _composeAvailable && _configPresent && _members.Count > 0;

    public bool CanPull => _composeAvailable && _configPresent;

    // Start, stop and restart act on containers, so they work with no plugin at all: that is what
    // keeps this tab useful on a host that has docker and nothing else.
    public bool CanStart => _members.Count > 0 && RunningCount < _members.Count;
    public bool CanStop => _members.Count > 0 && RunningCount > 0;
    public bool CanRestart => _members.Count > 0;

    /// <summary>Only a file VirtDeck wrote may be written back. A discovered one is shown and not edited.</summary>
    public bool CanEdit => _managed;

    public bool CanDelete => _managed;

    public DockerStackRow(DockerStackInfo info, bool composeAvailable)
    {
        Name = info.Name;
        Update(info, composeAvailable);
    }

    public void Update(DockerStackInfo info, bool composeAvailable)
    {
        Managed = info.Managed;
        ConfigFiles = info.ConfigFiles;
        ConfigPath = info.ConfigFiles.Count > 0 ? info.ConfigFiles[0] : "";
        ConfigPresent = info.ConfigPresent;
        WorkingDir = info.WorkingDir;

        // These four move together and none has a backing property of its own: starting or stopping
        // one container changes every cell in the row without anything about the stack itself
        // moving, which is why they are raised as a block.
        _members = info.Members;
        _membersKnown = info.MembersKnown;
        _composeAvailable = composeAvailable;

        Raise(nameof(Status));
        Raise(nameof(StatusTip));
        Raise(nameof(StateBrush));
        Raise(nameof(ServiceCount));
        Raise(nameof(ConfigTip));
        Raise(nameof(Members));
        Raise(nameof(MembersKnown));
    }

    /// <summary>The stack as the service wants it: what the commands are addressed to.</summary>
    public DockerStackInfo ToInfo() => new()
    {
        Name = Name,
        WorkingDir = _workingDir,
        ConfigFiles = ConfigFiles,
        ConfigPresent = _configPresent,
        Managed = _managed,
        Members = _members,
        MembersKnown = _membersKnown,
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
