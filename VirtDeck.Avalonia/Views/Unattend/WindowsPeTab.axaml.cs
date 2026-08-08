using Avalonia.Controls;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>
/// The "Windows PE stage" page: what runs before the machine first boots into Windows.
///
/// The destructive option is deliberately not the default. Replacing Windows Setup with the generated
/// script wipes the target disk with no confirmation, which is exactly right for the blank virtual disk
/// this wizard has just created and exactly wrong for a VM later pointed at existing storage, and the
/// same window is reachable from both.
/// </summary>
public partial class WindowsPeTab : UserControl, IUnattendTab
{
    public WindowsPeTab()
    {
        InitializeComponent();

        OptionBox.Fill(EditionBox, UnattendCatalog.WindowsEditions);
        OptionBox.Fill(InstallEditionBox, UnattendCatalog.WindowsEditions);

        PeDefaultRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        PeGenerateRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        PeScriptRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        EditionPickRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        EditionKeyRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        PartUnattendedRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        PartInteractiveRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        PartScriptRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        RecoveryCheck.IsCheckedChanged += (_, _) => UpdateEnabled();
        AssertGeneratedRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        AssertScriptRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        AssertMinSizeCheck.IsCheckedChanged += (_, _) => UpdateEnabled();
        AssertMaxSizeCheck.IsCheckedChanged += (_, _) => UpdateEnabled();
        InstallEditionRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        InstallIndexRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        InstallNameRadio.IsCheckedChanged += (_, _) => UpdateEnabled();

        Load(new UnattendConfig());
    }

    public void Load(UnattendConfig root)
    {
        var config = root.WindowsPe;

        PeDefaultRadio.IsChecked = config.Mode == WindowsPeMode.Default;
        PeGenerateRadio.IsChecked = config.Mode == WindowsPeMode.Generate;
        PeScriptRadio.IsChecked = config.Mode == WindowsPeMode.Script;
        PeScriptBox.Text = config.Script;

        EditionInteractiveRadio.IsChecked = config.Edition == EditionMode.Interactive;
        EditionPickRadio.IsChecked = config.Edition == EditionMode.Edition;
        EditionFirmwareRadio.IsChecked = config.Edition == EditionMode.Firmware;
        EditionKeyRadio.IsChecked = config.Edition == EditionMode.ProductKey;
        OptionBox.Select(EditionBox, config.EditionId);
        EditionKeyBox.Text = config.EditionProductKey;

        PartUnattendedRadio.IsChecked = config.Partitions == PartitionMode.Unattended;
        PartInteractiveRadio.IsChecked = config.Partitions == PartitionMode.Interactive;
        PartScriptRadio.IsChecked = config.Partitions == PartitionMode.Script;
        TargetDiskBox.Value = config.TargetDisk;
        LayoutBox.SelectedIndex = config.PartitionLayout switch
        {
            PartitionLayoutMode.Gpt => 1,
            PartitionLayoutMode.Mbr => 2,
            _ => 0,
        };
        RecoveryCheck.IsChecked = config.Recovery == RecoveryPartitionMode.Partition;
        SystemSizeBox.Value = config.SystemPartitionSize;
        RecoverySizeBox.Value = config.RecoveryPartitionSize;
        PartScriptBox.Text = config.PartitionScript;

        AssertGeneratedRadio.IsChecked = config.DiskAssertions == DiskAssertionMode.Generated;
        AssertSkipRadio.IsChecked = config.DiskAssertions == DiskAssertionMode.Skip;
        AssertScriptRadio.IsChecked = config.DiskAssertions == DiskAssertionMode.Script;
        AssertNoPartitionsCheck.IsChecked = config.AssertNoPartitions;
        AssertMinSizeCheck.IsChecked = config.AssertMinSize;
        MinSizeBox.Value = config.MinSizeGiB;
        AssertMaxSizeCheck.IsChecked = config.AssertMaxSize;
        MaxSizeBox.Value = config.MaxSizeGiB;
        AssertInterfaceCheck.IsChecked = config.AssertInterfaceType;
        AssertMediaCheck.IsChecked = config.AssertMediaType;
        AssertScriptBox.Text = config.DiskAssertionScript;

        InstallInteractiveRadio.IsChecked = config.InstallFrom == InstallFromMode.Interactive;
        InstallEditionRadio.IsChecked = config.InstallFrom == InstallFromMode.Edition;
        InstallIndexRadio.IsChecked = config.InstallFrom == InstallFromMode.Index;
        InstallNameRadio.IsChecked = config.InstallFrom == InstallFromMode.Name;
        OptionBox.Select(InstallEditionBox, config.InstallFromEditionId);
        InstallIndexBox.Value = config.InstallFromIndex;
        InstallNameBox.Text = config.InstallFromName;

        DisableDefenderCheck.IsChecked = config.DisableDefender;
        Disable8Dot3Check.IsChecked = config.Disable8Dot3Names;
        CompactOsCheck.IsChecked = config.CompactOs;
        SkipIntegrityCheck.IsChecked = config.SkipIntegrityCheck;
        PauseFormatCheck.IsChecked = config.PauseBeforeFormatting;
        PauseRebootCheck.IsChecked = config.PauseBeforeReboot;

        UpdateEnabled();
    }

    public void Apply(UnattendConfig root)
    {
        var config = root.WindowsPe;

        config.Mode =
            PeGenerateRadio.IsChecked == true ? WindowsPeMode.Generate :
            PeScriptRadio.IsChecked == true ? WindowsPeMode.Script :
            WindowsPeMode.Default;
        config.Script = PeScriptBox.Text ?? "";

        config.Edition =
            EditionPickRadio.IsChecked == true ? EditionMode.Edition :
            EditionFirmwareRadio.IsChecked == true ? EditionMode.Firmware :
            EditionKeyRadio.IsChecked == true ? EditionMode.ProductKey :
            EditionMode.Interactive;
        config.EditionId = OptionBox.IdOf(EditionBox);
        config.EditionProductKey = EditionKeyBox.Text ?? "";

        config.Partitions =
            PartInteractiveRadio.IsChecked == true ? PartitionMode.Interactive :
            PartScriptRadio.IsChecked == true ? PartitionMode.Script :
            PartitionMode.Unattended;
        config.TargetDisk = (int)(TargetDiskBox.Value ?? 0);
        config.PartitionLayout = LayoutBox.SelectedIndex switch
        {
            1 => PartitionLayoutMode.Gpt,
            2 => PartitionLayoutMode.Mbr,
            _ => PartitionLayoutMode.Automatic,
        };
        config.Recovery = RecoveryCheck.IsChecked == true
            ? RecoveryPartitionMode.Partition
            : RecoveryPartitionMode.None;
        config.SystemPartitionSize = (int)(SystemSizeBox.Value ?? 300);
        config.RecoveryPartitionSize = (int)(RecoverySizeBox.Value ?? 1000);
        config.PartitionScript = PartScriptBox.Text ?? "";

        config.DiskAssertions =
            AssertSkipRadio.IsChecked == true ? DiskAssertionMode.Skip :
            AssertScriptRadio.IsChecked == true ? DiskAssertionMode.Script :
            DiskAssertionMode.Generated;
        config.AssertNoPartitions = AssertNoPartitionsCheck.IsChecked == true;
        config.AssertMinSize = AssertMinSizeCheck.IsChecked == true;
        config.MinSizeGiB = (int)(MinSizeBox.Value ?? 100);
        config.AssertMaxSize = AssertMaxSizeCheck.IsChecked == true;
        config.MaxSizeGiB = (int)(MaxSizeBox.Value ?? 4000);
        config.AssertInterfaceType = AssertInterfaceCheck.IsChecked == true;
        config.AssertMediaType = AssertMediaCheck.IsChecked == true;
        config.DiskAssertionScript = AssertScriptBox.Text ?? "";

        config.InstallFrom =
            InstallEditionRadio.IsChecked == true ? InstallFromMode.Edition :
            InstallIndexRadio.IsChecked == true ? InstallFromMode.Index :
            InstallNameRadio.IsChecked == true ? InstallFromMode.Name :
            InstallFromMode.Interactive;
        config.InstallFromEditionId = OptionBox.IdOf(InstallEditionBox);
        config.InstallFromIndex = (int)(InstallIndexBox.Value ?? 1);
        config.InstallFromName = InstallNameBox.Text ?? "";

        config.DisableDefender = DisableDefenderCheck.IsChecked == true;
        config.Disable8Dot3Names = Disable8Dot3Check.IsChecked == true;
        config.CompactOs = CompactOsCheck.IsChecked == true;
        config.SkipIntegrityCheck = SkipIntegrityCheck.IsChecked == true;
        config.PauseBeforeFormatting = PauseFormatCheck.IsChecked == true;
        config.PauseBeforeReboot = PauseRebootCheck.IsChecked == true;
    }

    private void UpdateEnabled()
    {
        PeDefaultPanel.IsEnabled = PeDefaultRadio.IsChecked == true;
        PeScriptBox.IsEnabled = PeScriptRadio.IsChecked == true;
        GeneratePanel.IsEnabled = PeGenerateRadio.IsChecked == true;

        EditionBox.IsEnabled = EditionPickRadio.IsChecked == true;
        EditionKeyBox.IsEnabled = EditionKeyRadio.IsChecked == true;

        PartUnattendedPanel.IsEnabled = PartUnattendedRadio.IsChecked == true;
        PartScriptBox.IsEnabled = PartScriptRadio.IsChecked == true;

        // A recovery partition has a size only when there is one.
        bool recovery = RecoveryCheck.IsChecked == true;
        RecoverySizeLabel.IsEnabled = recovery;
        RecoverySizePanel.IsEnabled = recovery;

        // The generator refuses assertions together with hand partitioning, and it is right to: the
        // checks decide whether the script may go ahead on its own, which is not a question when
        // somebody is standing at the console. Say so instead of letting OK report it.
        bool byHand = PartInteractiveRadio.IsChecked == true;
        AssertPanel.IsEnabled = !byHand;
        AssertInteractiveNote.IsVisible = byHand;

        AssertGeneratedPanel.IsEnabled = AssertGeneratedRadio.IsChecked == true;
        AssertScriptBox.IsEnabled = AssertScriptRadio.IsChecked == true;
        MinSizeBox.IsEnabled = AssertMinSizeCheck.IsChecked == true;
        MaxSizeBox.IsEnabled = AssertMaxSizeCheck.IsChecked == true;

        InstallEditionBox.IsEnabled = InstallEditionRadio.IsChecked == true;
        InstallIndexBox.IsEnabled = InstallIndexRadio.IsChecked == true;
        InstallNameBox.IsEnabled = InstallNameRadio.IsChecked == true;
    }
}
