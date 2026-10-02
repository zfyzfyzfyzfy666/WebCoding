param([switch]$Yes,[switch]$PurgeSettings)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$root=[IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$bin=Join-Path $root 'bin'
$default=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'WebCoding')).TrimEnd('\')
try {
    if(!(Test-Path -LiteralPath (Join-Path $root '.webcoding-install'))){throw '缺少安装标记，拒绝删除文件。'}
    if(!$Yes) {if((Read-Host '卸载 WebCoding？不会删除你的项目，也不会卸载原先已有的公共依赖。输入 Y 继续') -notmatch '^[Yy]$'){exit 0}}
    foreach($p in @(Get-Process webcoding -ErrorAction SilentlyContinue)) {if($p.Path -eq (Join-Path $bin 'webcoding.exe')){throw '请先关闭 WebCoding 映射窗口后再卸载。'}}
    if($root -ieq $default) {
        $path=[Environment]::GetEnvironmentVariable('Path','User')
        $remaining=@($path -split ';'|Where-Object {$_ -and [Environment]::ExpandEnvironmentVariables($_.Trim().Trim('"')).TrimEnd('\') -ine $bin})
        [Environment]::SetEnvironmentVariable('Path',($remaining -join ';'),'User')
        Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WebCoding' -Recurse -Force -ErrorAction SilentlyContinue
    }
    # Only remove directories and files managed by this installer.
    foreach($name in @('bin','runtime')) { $p=Join-Path $root $name; if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Recurse -Force} }
    foreach($name in @('README.md','install-info.json','.webcoding-install','Uninstall.ps1')) { $p=Join-Path $root $name; if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Force} }
    if($PurgeSettings) {
        foreach($name in @('agent.md','tools.json','agent.md.bak','tools.json.bak','tunnel-provider.txt','.webcoding-settings')) {$p=Join-Path $root $name;if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Force}}
    } else {
        [IO.File]::WriteAllText((Join-Path $root '.webcoding-settings'),'WebCoding settings retained')
        Write-Output '已保留安装规则 agent.md 和 tools.json，重装会继续使用。项目目录从未删除。'
    }
    # Retain ownership metadata if logs or unrelated user files remain, so a clean-settings reinstall is still possible.
    if($PurgeSettings -and @(Get-ChildItem -LiteralPath $root -Force).Count -gt 0){[IO.File]::WriteAllText((Join-Path $root '.webcoding-settings'),'WebCoding uninstalled; preferences cleared; retained files')}
    if(@(Get-ChildItem -LiteralPath $root -Force).Count -eq 0){Remove-Item -LiteralPath $root -Force}else{Write-Output '安装目录内的其他文件已保留。'}
    Write-Output '卸载完成。请关闭旧终端后重新打开，以更新命令搜索路径。'
} catch {Write-Output ('卸载未完成：'+$_.Exception.Message);exit 1}
