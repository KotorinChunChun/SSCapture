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
AppMutex=Local\SSCapture.Singleton
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
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\SSCapture"; Filename: "{app}\SSCapture.exe"

[Run]
Filename: "{app}\SSCapture.exe"; Parameters: "--settings"; Description: "SSCaptureを起動する"; Flags: nowait postinstall skipifsilent
