#ifndef AppVersion
  #error AppVersionを指定してください
#endif
#ifndef SourceDir
  #error SourceDirを指定してください
#endif
#ifndef OutputDir
  #error OutputDirを指定してください
#endif

[Setup]
AppId=KotorinToys.SSCapture
AppName=SSCapture
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\SSCapture
DefaultGroupName=SSCapture
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0.22000
UsePreviousAppDir=yes
UninstallDisplayIcon={app}\SSCapture.exe
UninstallDisplayName=SSCapture
SetupMutex=Local\KotorinToys.SSCapture.Setup
CloseApplications=no
RestartApplications=no
OutputDir={#OutputDir}
OutputBaseFilename=SSCapture-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{tmp}\payload"; Flags: dontcopy recursesubdirs createallsubdirs
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\SSCapture"; Filename: "{app}\SSCapture.exe"

[Run]
Filename: "{app}\SSCapture.exe"; Parameters: "--settings"; Description: "SSCaptureを起動する"; Flags: nowait postinstall skipifsilent

[Code]
var
  Prepared, Completed, RemovalPrepared, RemovalCompleted: Boolean;
  Helper, Payload: String;

function Managed(const Operation: String; const Extra: String): Boolean;
var
  Code: Integer;
begin
  Result := Exec(Helper, '--product SSCapture --root "' + ExpandConstant('{app}') +
    '/" --operation ' + Operation + Extra, '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := Result and (Code = 0);
  Log('SSCapture Maintenance ' + Operation + ': ' + IntToStr(Code));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if Prepared then Exit;
  ExtractTemporaryFiles('{tmp}\payload\*');
  Payload := ExpandConstant('{tmp}\payload');
  Helper := Payload + '\maintenance\KotorinToys.Maintenance.exe';
  if not Managed('update', ' --payload "' + Payload + '/"') then
    Result := '導入先の照合または正常終了を確認できません。撮影・出力を完了して再試行してください。未完了の管理記録がある場合は復旧手順を確認してください。'
  else Prepared := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    if not Managed('observe-update', '') then
      RaiseException('配置されたファイルの照合に失敗しました。');
    #ifdef AcceptanceFault
    RaiseException('受入試験: ファイル配置後の失敗');
    #endif
    if not Managed('commit-update', '') then
      RaiseException('導入後の照合に失敗しました。復旧結果を確認してください。');
    Completed := True;
  end;
end;

procedure DeinitializeSetup;
begin
  if Prepared and not Completed then
    if not Managed('recover-update', '') then
      Log('復旧が完了していません。操作記録を保持しました。docs/recovery.mdを確認してください。');
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  if Prepared and not Completed then Result := 10;
end;

function CopyHelper(const Source, Target: String): Boolean;
var
  Find: TFindRec;
begin
  Result := ForceDirectories(Target);
  if not Result then Exit;
  if FindFirst(Source + '\*', Find) then begin
    try
      repeat
        if (Find.Name <> '.') and (Find.Name <> '..') then begin
          if (Find.Attributes and $400) <> 0 then begin Result := False; Exit; end;
          if (Find.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            Result := CopyHelper(Source + '\' + Find.Name, Target + '\' + Find.Name)
          else Result := FileCopy(Source + '\' + Find.Name, Target + '\' + Find.Name, True);
          if not Result then Exit;
        end;
      until not FindNext(Find);
    finally
      FindClose(Find);
    end;
  end else Result := False;
end;

function InitializeUninstall: Boolean;
begin
  // 削除対象の外へ依存物ごと退避し、部分削除後も予約を復元できるようにする。
  Payload := ExpandConstant('{tmp}\SSCapture-removal');
  Result := CopyHelper(ExpandConstant('{app}\maintenance'), Payload);
  if not Result then Exit;
  Helper := Payload + '\KotorinToys.Maintenance.exe';
  Result := Managed('remove', '');
  RemovalPrepared := Result;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usDone then begin
    RemovalCompleted := Managed('commit-remove', '');
    if not RemovalCompleted then RaiseException('予約整理の完了を記録できませんでした。復旧結果を確認してください。');
  end;
end;

procedure DeinitializeUninstall;
begin
  if RemovalPrepared and not RemovalCompleted then
    if not Managed('recover-remove', '') then
      Log('所有予約の復旧が完了していません。操作記録を保持しました。');
end;
