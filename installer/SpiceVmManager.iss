; Inno Setup script for SpiceVmManager.
; Installs the published app + bundled native USB DLLs, and silently installs the UsbDk
; kernel driver (required for client-side USB redirection).
;
; Build steps (run from the repo root):
;   1) Stage the native x64 DLLs into native\win-x64\  (see native\win-x64\VERSIONS.txt)
;   2) dotnet publish VmManager.App\VmManager.App.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
;   2b) Zip the SpiceClient source to publish\SpiceClient-src.zip (LGPL corresponding source)
;   3) Place UsbDk_1.0.22_x64.msi into installer\redist\
;   4) iscc installer\SpiceVmManager.iss
;
; publish.bat performs steps 2, 2b and 4 automatically.
;
; The app is published self-contained, so the .NET runtime is bundled — no runtime check needed.

#define AppName "SpiceVmManager"
#define AppPublisher "Leendert-Jan Floor"
#define AppVersion "1.0.0"
#define AppExe "VmManager.exe"
#define UsbDkMsi "UsbDk_1.0.22_x64.msi"
#define PublishDir "..\publish\win-x64"

[Setup]
AppId={{B5E9F2A1-7C3D-4E6B-9A2F-1D8C5E0A4B70}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
; Shown on the "License Agreement" page during setup (user must accept to continue).
LicenseFile=..\LICENSE.txt
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=output
OutputBaseFilename=SpiceVmManagerSetup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; A kernel driver (UsbDk) is installed, so elevation is required.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Sign the produced setup EXE in CI to avoid SmartScreen prompts (it elevates + installs a driver):
; SignTool=mysigntool $f

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; LGPL corresponding source for the SpiceClient library (the spice-html5-derived component),
; produced by publish.bat. Installed beside the app so the binary is "accompanied by source"
; (LGPL/GPL §3(a)) — no separate written offer or hosted URL required.
Source: "..\publish\SpiceClient-src.zip"; DestDir: "{app}"; Flags: ignoreversion
; The app's freeware EULA, plus third-party notices + license texts. We redistribute the
; LGPL/MIT native DLLs and the Apache-licensed UsbDk MSI, so their licenses ship beside them.
Source: "..\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\native\win-x64\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "redist\UsbDk-LICENSE-Apache-2.0.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "redist\{#UsbDkMsi}"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
var
  GNeedRestart: Boolean;

function UsbDkInstalled(): Boolean;
begin
  // The UsbDk MSI registers a kernel service "UsbDk" and a runtime library folder.
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\UsbDk') or
            DirExists(ExpandConstant('{commonpf}\UsbDk Runtime Library'));
end;

procedure InstallUsbDk();
var
  ResultCode: Integer;
  MsiPath: String;
begin
  if UsbDkInstalled() then
    exit;

  // UsbDk is a USB hub filter driver; installing it re-enumerates the USB bus, which briefly
  // disconnects/reconnects attached devices (removable drives may unmount and remount). Warn
  // first — like VirtualBox warns its network driver will reset connectivity.
  if MsgBox('SpiceVmManager will now install the UsbDk driver, which is required to redirect USB '
    + 'devices into virtual machines.' + #13#10#13#10
    + 'Installing this driver briefly disconnects and reconnects the USB devices on this PC. '
    + 'Removable drives may unmount and remount, so finish any transfers and safely close files '
    + 'on USB drives before continuing.' + #13#10#13#10
    + 'Install UsbDk now?', mbConfirmation, MB_YESNO) = IDNO then
  begin
    MsgBox('Skipped UsbDk installation. USB redirection will be unavailable until UsbDk is installed.'
      + #13#10 + 'You can re-run this installer later to install it.', mbInformation, MB_OK);
    exit;
  end;

  MsiPath := ExpandConstant('{tmp}\{#UsbDkMsi}');
  // Silent install, no automatic reboot. (Add `/l*v "{tmp}\UsbDk_install.log"` when debugging.)
  if not Exec('msiexec.exe', '/i "' + MsiPath + '" /qn /norestart', '',
              SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('Could not launch the UsbDk installer. USB redirection will be unavailable until '
      + 'UsbDk is installed.', mbError, MB_OK);
    exit;
  end;

  // 0 = ok, 3010 = success/reboot required, 1641 = success/reboot initiated.
  if (ResultCode = 3010) or (ResultCode = 1641) then
    GNeedRestart := True
  else if ResultCode <> 0 then
    MsgBox('UsbDk installation failed (exit code ' + IntToStr(ResultCode) + ').' + #13#10
      + 'USB redirection will not work until UsbDk is installed.', mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallUsbDk();
end;

// Called by Setup to decide whether to offer a reboot (UsbDk returned 3010/1641).
function NeedRestart(): Boolean;
begin
  Result := GNeedRestart;
end;
