param([Parameter(Mandatory=$true)][string]$SourceRoot,
      [Parameter(Mandatory=$true)][string]$SdkRoot,
      [Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$pinned = '0a60c025194a05e1d0994c3db53feac9ef33adbe'
if ((git -C $SourceRoot rev-parse HEAD).Trim() -ne $pinned) { throw "Use WebViewToolkit commit $pinned" }
if (git -C $SourceRoot status --porcelain) { throw 'Use a clean upstream source checkout.' }
$plugin = Join-Path $SourceRoot 'WebViewToolkitPlugin'
$sdk = Join-Path $SdkRoot 'build/native'
if (!(Test-Path (Join-Path $sdk 'include/WebView2.h'))) { throw 'Select the extracted Microsoft.Web.WebView2 1.0.4314.35 SDK.' }
$installation = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'Visual Studio C++ tools are required.' }
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
function Q([string]$value) { if ($value.Contains('"')) { throw 'Paths cannot contain quotes.' }; '"' + $value + '"' }
$sources = @('Plugin.cpp','PluginExports.cpp','WebViewManager.cpp','WebView.cpp','WebViewCapture.cpp','RenderAPI/RenderAPI.cpp','RenderAPI/RenderAPI_D3D11.cpp','RenderAPI/RenderAPI_D3D12.cpp')
$arguments = @('/nologo','/std:c++20','/EHsc','/MD','/LD','/utf-8','/DWEBVIEW_TOOLKIT_EXPORTS','/DNOMINMAX','/DWIN32_LEAN_AND_MEAN','/DUNICODE','/D_UNICODE','/DWEBVIEW_TOOLKIT_DX12_SUPPORT=1')
$includes = @('include','src','extern/Unity') | ForEach-Object { Join-Path $plugin $_ }
$includes += Join-Path $sdk 'include'
$arguments += $includes | ForEach-Object { '/I' + (Q $_) }
$arguments += $sources | ForEach-Object { Q (Join-Path $plugin ('src/' + $_)) }
$arguments += Q (Join-Path $PSScriptRoot 'SakuraWebViewPreview.cpp')
$arguments += @('/link','/OUT:WebViewToolkit.dll',('/DEF:'+(Q (Join-Path $plugin 'src/WebViewToolkit.def'))),(Q (Join-Path $sdk 'x64/WebView2LoaderStatic.lib')),'d3d11.lib','dxgi.lib','dcomp.lib','windowscodecs.lib','windowsapp.lib','shcore.lib','d3d12.lib','ole32.lib','user32.lib','advapi32.lib','shlwapi.lib','version.lib')
$script = '@echo off' + "`r`ncall " + (Q (Join-Path $installation 'Common7/Tools/VsDevCmd.bat')) + ' -arch=x64' + "`r`ncd /d " + (Q ([IO.Path]::GetFullPath($OutputRoot))) + "`r`ncl " + ($arguments -join ' ') + "`r`n"
$commandFile = Join-Path $OutputRoot 'build.cmd'
[IO.File]::WriteAllText($commandFile, $script, [Text.UTF8Encoding]::new($false))
& $commandFile
if ($LASTEXITCODE -ne 0) { throw "Native browser build failed: $LASTEXITCODE" }
Get-FileHash (Join-Path $OutputRoot 'WebViewToolkit.dll') -Algorithm SHA256
