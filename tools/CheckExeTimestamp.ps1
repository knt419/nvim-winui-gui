$ErrorActionPreference = 'Stop'

Write-Output '=== ユーザー様が見ていらっしゃる exe パス ==='
$exe = 'C:\Users\knt41\projects\nvim-winui-gui\src\App\bin\x64\Debug\net8.0-windows10.0.22621.0\win-x64\NvimWinUIGui.exe'

if (Test-Path -LiteralPath $exe) {
    $fi = Get-Item -LiteralPath $exe
    Write-Output ("EXISTS : " + $fi.FullName)
    Write-Output ("LastWrite : " + $fi.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))
    Write-Output ("Length    : " + $fi.Length + " bytes")
    $now = Get-Date
    Write-Output ("age       : " + [math]::Round(($now - $fi.LastWriteTime).TotalMinutes, 1) + " min")
} else {
    Write-Output ('MISSING: ' + $exe)
    Write-Output '--- bin 配下に存在する NvimWinUIGui.exe をすべて列挙 ---'
    Get-ChildItem 'C:\Users\knt41\projects\nvim-winui-gui\src\App\bin' -Recurse -File -Filter 'NvimWinUIGui.exe' -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Output ($_.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss') + '  ' + $_.FullName) }
}

Write-Output ''
Write-Output '=== 比較対象: ソース render.cs / multigrid.cs / csharpproj の最終更新 ==='
'C:\Users\knt41\projects\nvim-winui-gui\src\App\MainWindow.render.cs',
'C:\Users\knt41\projects\nvim-winui-gui\src\App\MainWindow.multigrid.cs',
'C:\Users\knt41\projects\nvim-winui-gui\src\App\NvimWinUIGui.csproj' |
  ForEach-Object {
    if (Test-Path -LiteralPath $_) {
        $fi = Get-Item -LiteralPath $_
        Write-Output ($fi.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss') + '  ' + $_.Substring(38))
    } else {
        Write-Output ('MISSING: ' + $_)
    }
  }

Write-Output ''
Write-Output '=== 判定: exe が「ソースより古い」= 変更が exe に反映されていない。==='
Write-Output '(head render.cs の LastWrite = 今日 14:12 台のはず。exe がそれより古いなら再ビルドが必要)'
