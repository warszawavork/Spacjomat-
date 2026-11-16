@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$d = '%~dp0'.TrimEnd('\'); Get-ChildItem -Path $d -File | Unblock-File -ErrorAction SilentlyContinue; $ws = New-Object -ComObject WScript.Shell; $desk = [Environment]::GetFolderPath('Desktop'); $lnk = $ws.CreateShortcut((Join-Path $desk 'Spacjomat.lnk')); $lnk.TargetPath = 'wscript.exe'; $lnk.Arguments = '\"' + (Join-Path $d 'Spacjomat.vbs') + '\"'; $lnk.WorkingDirectory = $d; if (Test-Path (Join-Path $d 'Spacjomat.ico')) { $lnk.IconLocation = (Join-Path $d 'Spacjomat.ico') }; $lnk.Save(); Write-Host 'Gotowe! Utworzono skrot Spacjomat na Pulpicie i uruchomiono aplikacje.'"
wscript.exe "%~dp0Spacjomat.vbs"
