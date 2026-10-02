param(
    [Parameter(Mandatory=$true)][string]$PayloadDirectory,
    [string]$InstallRoot=(Join-Path $env:LOCALAPPDATA 'WebCoding'),
    [switch]$CheckOnly,
    [switch]$EditConfiguration,
    [switch]$ForcePrivateRuntime,
    [switch]$NoRegistration,
    [ValidateSet("zh","en")][string]$Language="zh"
)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
$ProgressPreference='SilentlyContinue'
$DefaultRoot=Join-Path $env:LOCALAPPDATA 'WebCoding'
$InstallRoot=[IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$bin=Join-Path $InstallRoot 'bin'
$runtime=Join-Path $InstallRoot 'runtime'
$work=Join-Path ([IO.Path]::GetTempPath()) ('WebCoding-Download-'+[Guid]::NewGuid().ToString('N'))
$reboot=$false
function Status([string]$message) { Write-Output $message }
function Download([string]$url,[string]$destination) {
    if(!([Uri]$url).Scheme.Equals('https')){throw 'Only HTTPS downloads are allowed'}
    for($attempt=1;$attempt -le 3;$attempt++) {
        try {
            $client=New-Object Net.WebClient
            $client.Headers['User-Agent']='WebCoding-Setup/1.1'
            $client.DownloadFile($url,$destination)
            if((Get-Item -LiteralPath $destination).Length -eq 0){throw 'Empty download'}
            return
        } catch {if($attempt -eq 3){throw}; Start-Sleep -Seconds 2}
        finally {if($client){$client.Dispose()}}
    }
}
function GetJson([string]$url) {
    $client=New-Object Net.WebClient
    try {$client.Headers['User-Agent']='WebCoding-Setup/1.1'; return ($client.DownloadString($url)|ConvertFrom-Json)}
    finally {$client.Dispose()}
}
function VerifyHash([string]$file,[string]$expected) {
    if($expected -notmatch '^[a-fA-F0-9]{64}$'){throw 'Official SHA-256 checksum is missing or malformed'}
    if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $expected){throw 'Download SHA-256 verification failed'}
}
function Candidates([string]$name,[string]$privateDirectory) {
    $items=New-Object 'System.Collections.Generic.List[string]'
    $items.Add((Join-Path $privateDirectory $name))
    foreach($command in @(Get-Command $name -CommandType Application -ErrorAction SilentlyContinue)){if($command.Source){$items.Add($command.Source)}}
    $pf=[Environment]::GetFolderPath('ProgramFiles')
    $pf86=[Environment]::GetFolderPath('ProgramFilesX86')
    foreach($dir in @((Join-Path $pf 'nodejs'),(Join-Path $pf 'cloudflared'),(Join-Path $pf86 'cloudflared'),(Join-Path $pf 'Cloudflare'),(Join-Path $pf 'ngrok'),(Join-Path $pf86 'ngrok'))) {$items.Add((Join-Path $dir $name))}
    return @($items|Select-Object -Unique|Where-Object {Test-Path -LiteralPath $_ -PathType Leaf})
}
function NodeInfo([string]$file) {
    try {
        $data=(& $file -p 'JSON.stringify({version:process.version,arch:process.arch})' 2>$null)|ConvertFrom-Json
        if($LASTEXITCODE -eq 0 -and $data.arch -eq 'x64' -and $data.version -match '^v(\d+)\.' -and [int]$matches[1] -ge 18){return $data}
    }catch{}
    return $null
}
function CloudInfo([string]$file) {
    try {$text=(& $file --version 2>$null)-join ' '; if($LASTEXITCODE -eq 0 -and $text -match '^cloudflared version '){return $text}}catch{}
    return $null
}
function NgrokInfo([string]$file) {
    try {$text=(& $file version 2>$null)-join ' '; if($LASTEXITCODE -eq 0 -and $text -match '^ngrok version '){return $text}}catch{}
    return $null
}
function SetUserCommandPath([string]$entry) {
    $old=[Environment]::GetEnvironmentVariable('Path','User')
    $entries=@($old -split ';'|Where-Object {$_ -and $_.Trim()})
    $exists=@($entries|Where-Object {[Environment]::ExpandEnvironmentVariables($_.Trim().Trim('"')).TrimEnd('\') -ieq $entry.TrimEnd('\')}).Count -gt 0
    if(!$exists){[Environment]::SetEnvironmentVariable('Path',(($entries+@($entry))-join ';'),'User')}
    $env:Path=$entry+';'+$env:Path
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WebCodingEnvironment {
 [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] public static extern IntPtr SendMessageTimeout(IntPtr h,uint msg,IntPtr w,string l,uint flags,uint timeout,out IntPtr result);
}
'@
    $result=[IntPtr]::Zero
    [WebCodingEnvironment]::SendMessageTimeout([IntPtr]0xffff,0x001A,[IntPtr]::Zero,'Environment',2,3000,[ref]$result)|Out-Null
}
try {
    Status '正在检查运行环境...'
    $os=Get-CimInstance Win32_OperatingSystem
    $build=[int]$os.BuildNumber
    if(!$env:LOCALAPPDATA -or ![Environment]::Is64BitOperatingSystem -or $os.ProductType -ne 1 -or $build -lt 14393){throw '需要 Windows 10（1607 或更新）/ Windows 11 x64。安装器不会安装或升级操作系统。'}
    $dotnet=0
    foreach($key in @('HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full','HKLM:\SOFTWARE\WOW6432Node\Microsoft\NET Framework Setup\NDP\v4\Full')) {
        $v=Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        if($v -and $v.Release -gt $dotnet){$dotnet=[int]$v.Release}
    }
    $node=$null;$nodeVersion=$null;$cloud=$null;$cloudVersion=$null;$ngrok=$null;$ngrokVersion=$null
    if(!$ForcePrivateRuntime) {
        foreach($candidate in @(Candidates 'node.exe' (Join-Path $runtime 'node'))) { $info=NodeInfo $candidate; if($info){$node=$candidate;$nodeVersion=$info.version;break} }
        foreach($candidate in @(Candidates 'cloudflared.exe' (Join-Path $runtime 'cloudflared'))) { $info=CloudInfo $candidate; if($info){$cloud=$candidate;$cloudVersion=$info;break} }
        foreach($candidate in @(Candidates 'ngrok.exe' (Join-Path $runtime 'ngrok'))) { $info=NgrokInfo $candidate; if($info){$ngrok=$candidate;$ngrokVersion=$info;break} }
    }
    Status ('Windows x64：通过；系统版本 '+$os.Version)
    Status ('.NET Framework 4.8+：'+$(if($dotnet -ge 528040){'已安装'}else{'缺失，将安装'}))
    Status ('Node.js 18+ x64：'+$(if($node){$nodeVersion+'，复用现有安装'}else{'缺失或版本不符合，将安装官方 LTS'}))
    Status ('cloudflared：'+$(if($cloud){'已安装，复用现有安装'}else{'缺失，将安装官方版本'}))
    Status ('ngrok：'+$(if($ngrok){$ngrokVersion+'，复用现有安装'}else{'缺失，将安装官方版本'}))
    if($CheckOnly){Status 'CHECK_COMPLETE（只检测，未修改文件、环境变量或系统设置）'; exit 0}
    if(Test-Path -LiteralPath $InstallRoot) {
        if(!(Test-Path -LiteralPath (Join-Path $InstallRoot '.webcoding-install')) -and !(Test-Path -LiteralPath (Join-Path $InstallRoot '.webcoding-settings')) -and @(Get-ChildItem -LiteralPath $InstallRoot -Force).Count -gt 0){throw '安装目录已存在非本程序文件，为防止覆盖已停止安装。'}
    }
    foreach($p in @(Get-Process webcoding -ErrorAction SilentlyContinue)) {
        try {if($p.Path -eq (Join-Path $bin 'webcoding.exe')){throw '请先关闭正在运行的 webcoding 映射窗口，再重新安装。'}}catch {throw}
    }
    New-Item -ItemType Directory -Path $work -Force|Out-Null
    if($dotnet -lt 528040) {
        Status '正在下载微软 .NET Framework 4.8 离线安装程序...'
        $netInstaller=Join-Path $work 'dotnet48.exe'
        Download 'https://go.microsoft.com/fwlink/?linkid=2088631' $netInstaller
        $signature=Get-AuthenticodeSignature -FilePath $netInstaller
        if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation'){throw '微软安装包签名验证失败，未执行下载文件。'}
        Status '即将请求管理员授权安装 .NET；不会自动重启电脑。'
        $setup=Start-Process -FilePath $netInstaller -ArgumentList '/passive /norestart' -Verb RunAs -Wait -PassThru
        if($setup.ExitCode -notin @(0,3010,1641)){throw ('.NET 安装未成功，退出码：'+$setup.ExitCode)}
        $reboot=$setup.ExitCode -in @(3010,1641)
        $verified=Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue
        if(!$verified -or $verified.Release -lt 528040){throw '.NET 尚未就绪，请重启系统后再次运行安装器。'}
    }
    if(!$node) {
        Status '正在查询 Node.js 官方 LTS 版本...'
        $index=GetJson 'https://nodejs.org/dist/index.json'
        $release=$index|Where-Object {$_.lts -and [int]($_.version.Split('.')[0].TrimStart('v')) -ge 22 -and $_.files -contains 'win-x64-zip'}|Select-Object -First 1
        if(!$release -or $release.version -notmatch '^v\d+\.\d+\.\d+$'){throw '无法获取 Node.js 官方 LTS 版本信息'}
        $name='node-'+$release.version+'-win-x64.zip'
        $base='https://nodejs.org/dist/'+$release.version+'/'
        $sumfile=Join-Path $work 'SHASUMS256.txt';Download ($base+'SHASUMS256.txt') $sumfile
        $line=Get-Content -LiteralPath $sumfile|Where-Object {$_ -match ('\s'+[regex]::Escape($name)+'$')}|Select-Object -First 1
        if(!$line){throw 'Node.js 官方校验和列表中缺少所需文件'}
        $hash=($line -split '\s+')[0]
        Status ('正在下载 Node.js '+$release.version+' x64...')
        $zip=Join-Path $work $name;Download ($base+$name) $zip;VerifyHash $zip $hash
        $extract=Join-Path $work 'node';Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
        $nodeFolder=Join-Path $extract ('node-'+$release.version+'-win-x64')
        $node=Join-Path $nodeFolder 'node.exe';$nodeInfo=NodeInfo $node
        if(!$nodeInfo){throw '下载的 Node.js 未通过运行验证'}
        $nodeVersion=$nodeInfo.version
    }
    if(!$cloud) {
        Status '正在查询 cloudflared 官方发行版...'
        $asset=$null
        try {
            $release=GetJson 'https://api.github.com/repos/cloudflare/cloudflared/releases/latest'
            $asset=$release.assets|Where-Object {$_.name -eq 'cloudflared-windows-amd64.exe'}|Select-Object -First 1
            if(!$asset -or $asset.digest -notmatch '^sha256:[a-fA-F0-9]{64}$'){throw 'Missing official checksum'}
        } catch {
            # Pinned official fallback, recorded from the release metadata at build time.
            # This avoids making installation depend on the anonymous metadata API quota.
            Status '在线版本查询不可用，改用内置的官方校验版本 2026.9.3。'
            $asset=[pscustomobject]@{
                browser_download_url='https://github.com/cloudflare/cloudflared/releases/download/2026.9.3/cloudflared-windows-amd64.exe'
                digest='sha256:f096265ec2fcbe9bb6e2d64268db167ced3fcbb83d894bdb9e2fcdb26f2ea7e2'
            }
        }
        if($asset.digest -notmatch '^sha256:([a-fA-F0-9]{64})$'){throw '官方发行版未提供 SHA-256，已停止下载。'}
        $hash=$matches[1]
        if(!([string]$asset.browser_download_url).StartsWith('https://github.com/cloudflare/cloudflared/releases/download/')){throw '不受信任的下载地址'}
        Status '正在下载 cloudflared x64...'
        $cloud=Join-Path $work 'cloudflared.exe';Download $asset.browser_download_url $cloud;VerifyHash $cloud $hash
        $cloudVersion=CloudInfo $cloud
        if(!$cloudVersion){throw '下载的 cloudflared 未通过运行验证'}
    }
    if(!$ngrok) {
        Status '正在下载 ngrok 官方 Windows x64 版本...'
        $ngrokZip=Join-Path $work 'ngrok.zip'
        $ngrokDir=Join-Path $work 'ngrok'
        Download 'https://bin.equinox.io/c/bNyj1mQVY4c/ngrok-v3-stable-windows-amd64.zip' $ngrokZip
        Expand-Archive -LiteralPath $ngrokZip -DestinationPath $ngrokDir -Force
        $ngrok=Join-Path $ngrokDir 'ngrok.exe'
        if(!(Test-Path -LiteralPath $ngrok -PathType Leaf)){throw 'ngrok 官方压缩包中缺少 ngrok.exe'}
        $signature=Get-AuthenticodeSignature -FilePath $ngrok
        if($signature.Status -ne 'Valid' -or !$signature.SignerCertificate -or $signature.SignerCertificate.Subject -notmatch '(?i)ngrok'){throw 'ngrok 可执行文件签名验证失败，已停止安装。'}
        $ngrokVersion=NgrokInfo $ngrok
        if(!$ngrokVersion){throw '下载的 ngrok 未通过运行验证'}
    }
    $configStage=Join-Path $work 'config'
    New-Item -ItemType Directory -Path $configStage -Force|Out-Null
    foreach($name in @('agent.md','tools.json')) {
        $existing=Join-Path $InstallRoot $name
        $source=if(Test-Path -LiteralPath $existing){$existing}else{Join-Path $PayloadDirectory $name}
        Copy-Item -LiteralPath $source -Destination (Join-Path $configStage $name)
    }
    if($EditConfiguration) {
        Status '请在配置窗口中编辑规则和工具设置，保存后继续安装。'
        $editor=Start-Process -FilePath (Join-Path $PayloadDirectory 'WebCoding.Config.exe') -ArgumentList ('"'+$configStage+'"') -PassThru -Wait
        if($editor.ExitCode -eq 2){Status '用户取消安装。';exit 2}
        if($editor.ExitCode -ne 0){throw '配置编辑器失败，未覆盖现有安装。'}
    }
    Status '正在安装终端命令...'
    New-Item -ItemType Directory -Path $bin -Force|Out-Null
    [IO.File]::WriteAllText((Join-Path $InstallRoot '.webcoding-install'),'WebCoding 2.0.2',(New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $InstallRoot 'language.txt'),$Language,(New-Object Text.UTF8Encoding($false)))
    if($nodeFolder) {
        $nodeDest=Join-Path $runtime 'node';New-Item -ItemType Directory -Path $nodeDest -Force|Out-Null
        Copy-Item -Path (Join-Path $nodeFolder '*') -Destination $nodeDest -Recurse -Force
        $node=Join-Path $nodeDest 'node.exe'
    }
    if($cloud -eq (Join-Path $work 'cloudflared.exe')) {
        $cloudDest=Join-Path $runtime 'cloudflared';New-Item -ItemType Directory -Path $cloudDest -Force|Out-Null
        Copy-Item -LiteralPath $cloud -Destination (Join-Path $cloudDest 'cloudflared.exe') -Force
        $cloud=Join-Path $cloudDest 'cloudflared.exe'
    }
    if($ngrok -like ((Join-Path $work 'ngrok')+'*')) {
        $ngrokDest=Join-Path $runtime 'ngrok';New-Item -ItemType Directory -Path $ngrokDest -Force|Out-Null
        Copy-Item -LiteralPath $ngrok -Destination (Join-Path $ngrokDest 'ngrok.exe') -Force
        $ngrok=Join-Path $ngrokDest 'ngrok.exe'
    }
    foreach($name in @('webcoding.exe','WebCoding.Native.exe','WebCoding.Config.exe','WebCoding-Uninstall.exe')) {Copy-Item -LiteralPath (Join-Path $PayloadDirectory $name) -Destination (Join-Path $bin $name) -Force}
    foreach($name in @('agent.md','tools.json')) {
        $dest=Join-Path $InstallRoot $name
        if(!(Test-Path -LiteralPath $dest) -or $EditConfiguration) {
            if(Test-Path -LiteralPath $dest){Copy-Item -LiteralPath $dest -Destination ($dest+'.bak') -Force}
            Copy-Item -LiteralPath (Join-Path $configStage $name) -Destination $dest -Force
        }
    }
    Remove-Item -LiteralPath (Join-Path $InstallRoot '.webcoding-settings') -Force -ErrorAction SilentlyContinue
    foreach($name in @('Uninstall.ps1','README.md')){Copy-Item -LiteralPath (Join-Path $PayloadDirectory $name) -Destination (Join-Path $InstallRoot $name) -Force}
    @{version='2.0.2';installedAt=(Get-Date -Format o);node=$node;nodeVersion=$nodeVersion;cloudflared=$cloud;cloudflaredVersion=$cloudVersion;ngrok=$ngrok;ngrokVersion=$ngrokVersion;rebootRequired=$reboot}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $InstallRoot 'install-info.json') -Encoding UTF8
    if(!$NoRegistration) {
        SetUserCommandPath $bin
        $key='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WebCoding'
        New-Item -Path $key -Force|Out-Null
        $uninstallExe=Join-Path $bin 'WebCoding-Uninstall.exe'
        $uninstall='"'+$uninstallExe+'"'
        foreach($entry in @{DisplayName='WebCoding';DisplayVersion='2.0.2';InstallLocation=$InstallRoot;UninstallString=$uninstall;DisplayIcon=(Join-Path $bin 'webcoding.exe')}.GetEnumerator()) {New-ItemProperty -Path $key -Name $entry.Key -Value $entry.Value -PropertyType String -Force|Out-Null}
        New-ItemProperty -Path $key -Name NoModify -Value 1 -PropertyType DWord -Force|Out-Null
        New-ItemProperty -Path $key -Name NoRepair -Value 1 -PropertyType DWord -Force|Out-Null
    }
    Status '安装成功。'
    Status ('安装位置：'+$InstallRoot)
    Status '关闭旧终端后重新打开，在任意项目文件夹输入 webcoding 即可映射当前目录。'
    if($reboot){Status '注意：.NET 安装要求重启，请先重启电脑。'}
    Status 'INSTALL_SUCCESS'
    if($reboot){exit 3010}else{exit 0}
} catch {
    Write-Output ('安装失败：'+$_.Exception.Message)
    Write-Output '未能完成的步骤不会被标记为安装成功。修复网络或授权问题后可重新运行安装器。'
    exit 1
} finally {if(Test-Path -LiteralPath $work){Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue}}
