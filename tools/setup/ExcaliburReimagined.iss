; Excalibur Reimagined setup (A9). Built by tools\scripts\make-setup.ps1, which publishes the programs (self-contained, win-x64)
; into {#Staging} and passes the version:  ISCC /DAppVersion=0.1.7 /DStaging=...\program ExcaliburReimagined.iss
; The setup copies the files, makes the shortcuts and, when PawnIO is missing, downloads the official PawnIO setup, checks its
; SHA-256 and installs it silently. setup-helper.ps1 does the services, the data folder rights and NoteBook FanControl.
; Uninstall: services removed (the fan service hands the fans to the EC as it stops), settings and history kept unless the user
; asks for them to go.

#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z (make-setup.ps1 does).
#endif
#ifndef Staging
  #error Pass /DStaging=<folder with Excalibur.FanService, Excalibur.FanGuardian, Excalibur.App> (make-setup.ps1 does).
#endif

#define AppName "Excalibur Reimagined"
#define AppExe "Excalibur.App\ExcaliburReimagined.exe"
#define PawnIoUrl "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe"
#define PawnIoSha256 "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032"

[Setup]
AppId={{6E0B3F0A-5C3B-4E7E-9A51-2C9E1B7D4F21}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=soldsawn
AppPublisherURL=https://github.com/soldsawn/ExcaliburReimagined
AppSupportURL=https://github.com/soldsawn/ExcaliburReimagined/issues
AppUpdatesURL=https://github.com/soldsawn/ExcaliburReimagined/releases
; The same folder the scripts and the zip package use, so an earlier install is updated in place.
DefaultDirName={autopf}\ExcaliburReimagined
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\..\dist
OutputBaseFilename=ExcaliburReimagined-{#AppVersion}-setup
SetupIconFile=..\..\src\Excalibur.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The helper closes the app and stops the services itself (PrepareToInstall), before any file is replaced.
CloseApplications=no
RestartApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
turkish.PawnIoTitle=PawnIO sürücüsü
turkish.PawnIoText=Fan denetleyicisine güvenli erişim için PawnIO sürücüsü indirilip kuruluyor (resmî dosya, imzası denetlenir).
turkish.PawnIoBadHash=İndirilen PawnIO dosyası beklenen dosya değil (SHA-256 uyuşmadı); kurulmadı.
turkish.PawnIoFailed=PawnIO kurulamadı (kod %1). İnternet bağlantınızı denetleyip kurulumu yeniden çalıştırın ya da PawnIO'yu https://pawnio.eu adresinden kendiniz kurun.
turkish.NbfcAsk=Bu bilgisayarda NoteBook FanControl kurulu. O da fanları yönetiyor; ikisi birlikte çalışırsa fanlar birbirine karışır.%n%nNoteBook FanControl hizmeti kapatılsın mı? (Excalibur Reimagined'ı kaldırınca eski haline döner.)
turkish.ServicesFailed=Fan hizmetleri kurulamadı. Ayrıntı: %1
turkish.Stopping=Çalışan Excalibur Reimagined hizmetleri durduruluyor...
turkish.Services=Fan hizmetleri kuruluyor...
turkish.DeleteData=Ayarlarınız, fan geçmişi ve günlükler de silinsin mi?%n%nHayır derseniz yeniden kurduğunuzda her şey yerinde olur.
turkish.OpenApp=Excalibur Reimagined'ı aç
english.PawnIoTitle=PawnIO driver
english.PawnIoText=Downloading and installing the PawnIO driver, for safe access to the fan controller (the official file; its hash is checked).
english.PawnIoBadHash=The downloaded PawnIO file is not the expected one (SHA-256 mismatch); it was not installed.
english.PawnIoFailed=PawnIO could not be installed (code %1). Check the internet connection and run the setup again, or install PawnIO yourself from https://pawnio.eu.
english.NbfcAsk=NoteBook FanControl is installed on this computer. It controls the fans too; with both running the fans would be fought over.%n%nTurn the NoteBook FanControl service off? (It comes back as it was when Excalibur Reimagined is uninstalled.)
english.ServicesFailed=The fan services could not be installed. Details: %1
english.Stopping=Stopping the running Excalibur Reimagined services...
english.Services=Installing the fan services...
english.DeleteData=Delete your settings, the fan history and the logs as well?%n%nIf you say No, everything is still there when you install again.
english.OpenApp=Open Excalibur Reimagined

[InstallDelete]
; Start clean: a leftover from a framework-dependent build (tools\scripts\install-fan.ps1) or an older layout must not mix in.
Type: filesandordirs; Name: "{app}\Excalibur.FanService"
Type: filesandordirs; Name: "{app}\Excalibur.FanGuardian"
Type: filesandordirs; Name: "{app}\Excalibur.App"
Type: filesandordirs; Name: "{app}\Excalibur.FanTestUi"
Type: filesandordirs; Name: "{app}\Excalibur.Tray"
; Earlier installs put the tray shortcut in the installing user's Startup folder; it is the common one now.
Type: files; Name: "{userstartup}\Excalibur Reimagined.lnk"

[Files]
Source: "{#Staging}\Excalibur.FanService\*"; DestDir: "{app}\Excalibur.FanService"; Flags: recursesubdirs ignoreversion
Source: "{#Staging}\Excalibur.FanGuardian\*"; DestDir: "{app}\Excalibur.FanGuardian"; Flags: recursesubdirs ignoreversion
Source: "{#Staging}\Excalibur.App\*"; DestDir: "{app}\Excalibur.App"; Flags: recursesubdirs ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"
Source: "..\pawnio-modules\COPYING"; DestDir: "{app}"; DestName: "PawnIO-modules-LICENSE.txt"
Source: "setup-helper.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Start menu: the window; Windows sign-in (every user): the tray icon.
Name: "{commonprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "--window"; WorkingDir: "{app}\Excalibur.App"
Name: "{commonstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}\Excalibur.App"

[Run]
; As the signed-in user, not the elevated setup: first the tray, then (if ticked) the window.
Filename: "{app}\{#AppExe}"; Flags: runasoriginaluser nowait; StatusMsg: "{cm:OpenApp}"
Filename: "{app}\{#AppExe}"; Parameters: "--window"; Description: "{cm:OpenApp}"; Flags: runasoriginaluser nowait postinstall skipifsilent

[UninstallRun]
; Before the files go: stop and delete the services (the fan hands back to the EC), NoteBook FanControl back as it was.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\setup-helper.ps1"" -Action Uninstall -InstallDir ""{app}"""; Flags: runhidden waituntilterminated; RunOnceId: "ExcaliburUninstall"

[Code]
var
  PawnIoPage: TDownloadWizardPage;
  DisableNbfc: Boolean;
  PawnIoReboot: Boolean;

function PawnIoInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf64}\PawnIO\PawnIOLib.dll'));
end;

function NbfcInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\NbfcService');
end;

function Helper(const Action: String; var Code: Integer): Boolean;
var
  Args: String;
begin
  Args := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\setup-helper.ps1') + '" -Action ' + Action +
    ' -InstallDir "' + ExpandConstant('{app}') + '"';
  if DisableNbfc then Args := Args + ' -DisableNbfc';
  Result := Exec('powershell.exe', Args, '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

function HelperLogTail: String;
var
  Lines: TArrayOfString;
begin
  Result := '';
  if LoadStringsFromFile(ExpandConstant('{%TEMP}\ExcaliburReimagined-setup.log'), Lines) and (GetArrayLength(Lines) > 0) then
    Result := Lines[GetArrayLength(Lines) - 1];
end;

procedure InitializeWizard;
begin
  PawnIoPage := CreateDownloadPage(CustomMessage('PawnIoTitle'), CustomMessage('PawnIoText'), nil);
end;

// On "Install": PawnIO first (download, hash check, silent install), then the NoteBook FanControl question.
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Code: Integer;
  Setup: String;
begin
  Result := True;
  if CurPageID <> wpReady then Exit;

  if not PawnIoInstalled then
  begin
    PawnIoPage.Clear;
    PawnIoPage.Add('{#PawnIoUrl}', 'PawnIO_setup.exe', '{#PawnIoSha256}');
    PawnIoPage.Show;
    try
      try
        PawnIoPage.Download; // raises on a failed download or a hash mismatch
      except
        if Pos('SHA', Uppercase(GetExceptionMessage)) > 0 then
          MsgBox(CustomMessage('PawnIoBadHash'), mbCriticalError, MB_OK)
        else
          MsgBox(FmtMessage(CustomMessage('PawnIoFailed'), [GetExceptionMessage]), mbCriticalError, MB_OK);
        Result := False;
        Exit;
      end;
      Setup := ExpandConstant('{tmp}\PawnIO_setup.exe');
      // 0 = installed, 3010 = installed, a restart is needed (winget's manifest for PawnIO 2.2.0).
      if not Exec(Setup, '-install -silent', '', SW_HIDE, ewWaitUntilTerminated, Code) or ((Code <> 0) and (Code <> 3010))
        or not PawnIoInstalled then
      begin
        MsgBox(FmtMessage(CustomMessage('PawnIoFailed'), [IntToStr(Code)]), mbCriticalError, MB_OK);
        Result := False;
        Exit;
      end;
      PawnIoReboot := Code = 3010;
    finally
      PawnIoPage.Hide;
    end;
  end;

  DisableNbfc := NbfcInstalled and
    (SuppressibleMsgBox(CustomMessage('NbfcAsk'), mbConfirmation, MB_YESNO, IDYES) = IDYES);
end;

// Before any file is replaced: stop what runs (an update over a running install).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  Args: String;
begin
  Result := '';
  if not FileExists(ExpandConstant('{app}\setup-helper.ps1')) then
  begin
    // A first install, or one made by the scripts: stop the services by name if they exist.
    Exec('net.exe', 'stop ExcaliburFanGuardian', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Exec('net.exe', 'stop ExcaliburFan', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Exec('taskkill.exe', '/F /IM ExcaliburReimagined.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Exit;
  end;
  WizardForm.PreparingLabel.Caption := CustomMessage('Stopping');
  Args := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\setup-helper.ps1') + '" -Action Stop';
  Exec('powershell.exe', Args, '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  WizardForm.StatusLabel.Caption := CustomMessage('Services');
  if not Helper('Install', Code) then
    MsgBox(FmtMessage(CustomMessage('ServicesFailed'), [HelperLogTail]), mbCriticalError, MB_OK);
end;

function NeedRestart: Boolean;
begin
  Result := PawnIoReboot;
end;

// After the files are gone: settings, history and logs only if the user says so (default: keep).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  if SuppressibleMsgBox(CustomMessage('DeleteData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
  begin
    DelTree(ExpandConstant('{commonappdata}\ExcaliburReimagined'), True, True, True);
    DelTree(ExpandConstant('{userappdata}\ExcaliburReimagined'), True, True, True);
  end;
end;
