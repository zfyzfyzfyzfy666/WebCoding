param([string]$SetupFileName='WebCoding-Setup.exe')
$ErrorActionPreference='Stop'
$fw=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc=Join-Path $fw 'csc.exe'
$output=Join-Path (Split-Path $PSScriptRoot -Parent) 'WebCoding-2.0.2-Release'
$tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
$stage=[IO.Path]::GetFullPath((Join-Path $tempRoot ('WebCoding-Build-'+[Guid]::NewGuid().ToString('N'))))
if(!$stage.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $stage -Leaf) -notmatch '^WebCoding-Build-[a-f0-9]{32}$'){throw 'Invalid temporary build directory'}
$payload=Join-Path $stage 'payload'
try {
& (Join-Path $PSScriptRoot 'Build-Branding.ps1') -OutputDirectory (Join-Path $stage 'assets')
$icon='/win32icon:'+(Join-Path $stage 'assets\webcoding.ico')
$logo='/resource:'+(Join-Path $stage 'assets\logo-ui.png')+',WebCoding.Logo'
$branding=Join-Path $PSScriptRoot 'Branding.cs'
New-Item -ItemType Directory -Path $payload -Force|Out-Null
$refs=@('System.dll','System.Core.dll','System.Windows.Forms.dll','System.Drawing.dll')|ForEach-Object {'/r:'+(Join-Path $fw $_)}
& $csc /nologo /noconfig /target:exe /platform:x64 /optimize+ /debug- /utf8output $icon ('/out:'+(Join-Path $payload 'webcoding.exe')) ('/resource:'+(Join-Path $PSScriptRoot 'bridge.js')+',WebCoding.bridge.js') $refs (Join-Path $PSScriptRoot 'Program.cs') (Join-Path $PSScriptRoot 'TunnelLog.cs')
if($LASTEXITCODE -ne 0){throw 'Command compilation failed'}
$nativeRefs=@('System.dll','System.Core.dll','System.Windows.Forms.dll','System.Drawing.dll','System.Web.Extensions.dll')|ForEach-Object {'/r:'+(Join-Path $fw $_)}
& $csc /nologo /noconfig /target:exe /platform:x64 /optimize+ /debug- /utf8output $icon ('/out:'+(Join-Path $payload 'WebCoding.Native.exe')) $nativeRefs (Join-Path $PSScriptRoot 'Native.cs')
if($LASTEXITCODE -ne 0){throw 'Native helper compilation failed'}
& $csc /nologo /noconfig /target:winexe /platform:x64 /optimize+ /debug- /utf8output $icon $logo ('/out:'+(Join-Path $payload 'WebCoding.Config.exe')) ('/resource:'+(Join-Path $PSScriptRoot 'tools.json')+',WebCoding.ConfigDefaults') $nativeRefs $branding (Join-Path $PSScriptRoot 'Config.cs')
if($LASTEXITCODE -ne 0){throw 'Configuration editor compilation failed'}
& $csc /nologo /noconfig /target:winexe /platform:x64 /optimize+ /debug- /utf8output $icon $logo ('/out:'+(Join-Path $payload 'WebCoding-Uninstall.exe')) $nativeRefs $branding (Join-Path $PSScriptRoot 'Uninstaller.cs')
if($LASTEXITCODE -ne 0){throw 'Uninstaller compilation failed'}
foreach($name in @('Install.ps1','Uninstall.ps1','README.md','agent.md','tools.json')){
    $source=Join-Path $PSScriptRoot $name
    $destination=Join-Path $payload $name
    if($name.EndsWith('.ps1')){
        [IO.File]::WriteAllText($destination,[IO.File]::ReadAllText($source,[Text.Encoding]::UTF8),(New-Object Text.UTF8Encoding($true)))
$tokens=$null; $parseErrors=$null; [System.Management.Automation.Language.Parser]::ParseFile($destination,[ref]$tokens,[ref]$parseErrors)|Out-Null; if($parseErrors.Count){throw ($name+' parsing failed: '+($parseErrors.Message -join '; '))}
    }else{Copy-Item -LiteralPath $source -Destination $destination -Force}
}
$resources=@('webcoding.exe','WebCoding.Native.exe','WebCoding.Config.exe','WebCoding-Uninstall.exe','Install.ps1','Uninstall.ps1','README.md','agent.md','tools.json')|ForEach-Object {'/resource:'+(Join-Path $payload $_)+',WebCoding.Setup.'+$_}
Copy-Item -LiteralPath (Join-Path $payload 'WebCoding-Uninstall.exe') -Destination (Join-Path $output 'WebCoding-Uninstall.exe') -Force
$readme=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'README.md'),[Text.Encoding]::UTF8)
$buildSection=$readme.IndexOf('## '+[char]0x6e90+[char]0x7801+[char]0x6784+[char]0x5efa,[StringComparison]::Ordinal)
if($buildSection -ge 0){$readme=$readme.Substring(0,$buildSection).TrimEnd()+[Environment]::NewLine}
[IO.File]::WriteAllText((Join-Path $output 'README.md'),$readme,(New-Object Text.UTF8Encoding($false)))
$setup=Join-Path $output $SetupFileName
& $csc /nologo /noconfig /target:exe /platform:x64 /optimize+ /debug- /utf8output $icon $logo ('/out:'+$setup) $refs $resources $branding (Join-Path $PSScriptRoot 'Setup.cs')
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed'}
Write-Output ('SETUP_BYTES='+(Get-Item -LiteralPath $setup).Length)
Write-Output ('COMMAND_BYTES='+(Get-Item -LiteralPath (Join-Path $payload 'webcoding.exe')).Length)
} finally {
    if([IO.Directory]::Exists($stage)){[IO.Directory]::Delete($stage,$true)}
}
