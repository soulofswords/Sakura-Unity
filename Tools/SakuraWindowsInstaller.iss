#ifndef Edition
  #error Specify Edition as Companion or VTuber.
#endif
#if Edition == "Companion"
  #define OtherEdition "VTuber"
  #define EditionId "{{32FAB995-920F-4B4E-BF4D-D98015B91E14}"
  #define IconFile "companion.ico"
#elif Edition == "VTuber"
  #define OtherEdition "Companion"
  #define EditionId "{{81739DB2-8043-462A-92C7-7FF30B39B24F}"
  #define IconFile "vtuber.ico"
#else
  #error Unknown Sakura edition.
#endif
#define Product "Sakura " + Edition
#define Executable Product + ".exe"

[Setup]
AppId={#EditionId}
AppName={#Product}
AppVersion={#Version}
AppPublisher=Sakura
DefaultDirName={localappdata}\Programs\{#Product}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#Executable}
SetupIconFile={#Payload}\Sakura\launcher\{#IconFile}
OutputBaseFilename=Sakura-{#Edition}-{#Version}-Windows-x64-Setup
WizardStyle=modern
Compression=lzma2/fast
SolidCompression=yes

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Excludes: "\Sakura\settings.json"; Flags: ignoreversion recursesubdirs
Source: "{#Payload}\Sakura\settings.json"; DestDir: "{app}\Sakura"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "..\Packages\com.vrmc.vrm\LICENSE.txt"; DestDir: "{app}\Licenses"; DestName: "UniVRM.txt"
Source: "..\Packages\com.vrmc.gltf\LICENSE.md"; DestDir: "{app}\Licenses"; DestName: "UniGLTF.md"
Source: "..\Packages\com.yohawing.mmd-loader\LICENSE"; DestDir: "{app}\Licenses"; DestName: "MMD Loader.txt"
Source: "..\Packages\com.webviewtoolkit.core\LICENSE"; DestDir: "{app}\Licenses"; DestName: "WebView Toolkit.txt"
Source: "..\Assets\Live2D\Cubism\LICENSE.md"; DestDir: "{app}\Licenses"; DestName: "Live2D Cubism.md"
Source: "..\Assets\Live2D\Cubism\Plugins\LICENSE.md"; DestDir: "{app}\Licenses"; DestName: "Live2D Cubism Core.md"

[Icons]
Name: "{autoprograms}\{#Product}"; Filename: "{app}\{#Executable}"

[Run]
Filename: "{app}\{#Executable}"; Description: "Launch {#Product}"; Flags: nowait postinstall skipifsilent

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\Sakura {#OtherEdition}.exe')) then
    Result := 'Choose a separate folder for {#Product}. This folder already contains Sakura {#OtherEdition}.';
end;
