using System.Collections.ObjectModel;
using Avalonia.Controls;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>
/// The "User accounts" page of <see cref="UnattendWindow"/>: which local accounts Setup creates, who
/// gets logged on afterwards, and the two account policies Windows has no unattend element for.
///
/// The account table has a fixed five rows, the way the reference generator (schneegans.de) presents
/// it. Unused rows are left blank and dropped in <see cref="Apply"/>, which is cheaper for both the
/// user and the code than an add/remove list for something nobody fills more than twice.
/// </summary>
public partial class UserAccountsTab : UserControl
{
    private const int AccountRowCount = 5;

    private readonly ObservableCollection<LocalAccountRow> _accounts = new();

    public UserAccountsTab()
    {
        InitializeComponent();
        AccountList.ItemsSource = _accounts;

        // The dependent editors follow their radio button rather than being separately disable-able,
        // so there is never a value on screen that the selected option ignores. Everything the
        // accounts table needs hangs off one panel, so the whole subtree greys out in one assignment.
        AccountsTableRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        LogonFirstAdminRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        LogonAdministratorRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        PasswordCustomRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        LockoutCustomRadio.IsCheckedChanged += (_, _) => UpdateEnabled();

        Load(new UserAccountsConfig());
    }

    /// <summary>Fills the page from a config; the table is padded out to its fixed row count.</summary>
    public void Load(UserAccountsConfig config)
    {
        AccountsTableRadio.IsChecked = config.AccountCreation == AccountCreationMode.LocalAccounts;
        MicrosoftAccountRadio.IsChecked = config.AccountCreation == AccountCreationMode.MicrosoftAccountInteractive;
        LocalAccountRadio.IsChecked = config.AccountCreation == AccountCreationMode.LocalAccountInteractive;

        _accounts.Clear();
        foreach (var account in config.Accounts) _accounts.Add(new LocalAccountRow(account));
        while (_accounts.Count < AccountRowCount) _accounts.Add(new LocalAccountRow());
        // Editing a row can change whether the first-logon choice is answerable, so the page listens
        // to the rows it just built.
        foreach (var row in _accounts) row.PropertyChanged += (_, _) => UpdateEnabled();

        LogonFirstAdminRadio.IsChecked = config.FirstLogon == FirstLogonMode.FirstAdminAccount;
        LogonAdministratorRadio.IsChecked = config.FirstLogon == FirstLogonMode.BuiltInAdministrator;
        LogonNoneRadio.IsChecked = config.FirstLogon == FirstLogonMode.None;
        AdminPasswordBox.Text = config.AdministratorPassword;

        PasswordNeverRadio.IsChecked = config.PasswordExpiry == PasswordExpiryMode.Never;
        PasswordDefaultRadio.IsChecked = config.PasswordExpiry == PasswordExpiryMode.WindowsDefault;
        PasswordCustomRadio.IsChecked = config.PasswordExpiry == PasswordExpiryMode.Custom;
        PasswordDaysBox.Value = config.PasswordExpiryDays;

        LockoutDefaultRadio.IsChecked = config.Lockout == LockoutMode.Default;
        LockoutDisabledRadio.IsChecked = config.Lockout == LockoutMode.Disabled;
        LockoutCustomRadio.IsChecked = config.Lockout == LockoutMode.Custom;
        LockoutThresholdBox.Value = config.LockoutThreshold;
        LockoutWindowBox.Value = config.LockoutWindowMinutes;
        LockoutDurationBox.Value = config.LockoutDurationMinutes;

        ObscurePasswordsCheck.IsChecked = config.ObscurePasswords;

        UpdateEnabled();
    }

    /// <summary>Writes the page back into a config. Blank table rows are not accounts.</summary>
    public void Apply(UserAccountsConfig config)
    {
        config.AccountCreation =
            LocalAccountRadio.IsChecked == true ? AccountCreationMode.LocalAccountInteractive :
            AccountsTableRadio.IsChecked == true ? AccountCreationMode.LocalAccounts :
            AccountCreationMode.MicrosoftAccountInteractive;

        // Kept even in the interactive modes, so switching back and forth does not empty the table.
        // The generator ignores them unless the mode says otherwise.
        config.Accounts = _accounts.Where(r => !r.IsEmpty).Select(r => r.ToAccount()).ToList();

        config.FirstLogon =
            LogonAdministratorRadio.IsChecked == true ? FirstLogonMode.BuiltInAdministrator :
            LogonNoneRadio.IsChecked == true ? FirstLogonMode.None :
            FirstLogonMode.FirstAdminAccount;
        config.AdministratorPassword = AdminPasswordBox.Text ?? "";

        config.PasswordExpiry =
            PasswordDefaultRadio.IsChecked == true ? PasswordExpiryMode.WindowsDefault :
            PasswordCustomRadio.IsChecked == true ? PasswordExpiryMode.Custom :
            PasswordExpiryMode.Never;
        config.PasswordExpiryDays = (int)(PasswordDaysBox.Value ?? 42);

        config.Lockout =
            LockoutDisabledRadio.IsChecked == true ? LockoutMode.Disabled :
            LockoutCustomRadio.IsChecked == true ? LockoutMode.Custom :
            LockoutMode.Default;
        config.LockoutThreshold = (int)(LockoutThresholdBox.Value ?? 10);
        config.LockoutWindowMinutes = (int)(LockoutWindowBox.Value ?? 10);
        config.LockoutDurationMinutes = (int)(LockoutDurationBox.Value ?? 10);

        config.ObscurePasswords = ObscurePasswordsCheck.IsChecked == true;
    }

    private void UpdateEnabled()
    {
        AccountsTablePanel.IsEnabled = AccountsTableRadio.IsChecked == true;
        AdminPasswordBox.IsEnabled = LogonAdministratorRadio.IsChecked == true;
        NoAdminNote.IsVisible = LogonFirstAdminRadio.IsChecked == true &&
                                !_accounts.Any(r => !r.IsEmpty && r.IsAdministrator);
        PasswordDaysBox.IsEnabled = PasswordCustomRadio.IsChecked == true;
        LockoutThresholdBox.IsEnabled = LockoutCustomRadio.IsChecked == true;
        LockoutWindowBox.IsEnabled = LockoutCustomRadio.IsChecked == true;
        LockoutDurationBox.IsEnabled = LockoutCustomRadio.IsChecked == true;
    }
}
