@echo off
cd /d "%~dp0"
echo.
echo  Naprawa Spacjomatu: zamykam stare kopie, odblokowuje pliki, uruchamiam od nowa...
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'powershell.exe' -and $_.CommandLine -like '*Spacjomat.ps1*' -and $_.ProcessId -ne $PID } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; Get-ChildItem -Path '%~dp0' -Recurse -File | Unblock-File -ErrorAction SilentlyContinue; Remove-Item '%~dp0log.txt','%~dp0log.old.txt' -ErrorAction SilentlyContinue"
timeout /t 1 /nobreak >nul
wscript.exe "%~dp0Spacjomat.vbs"
echo  Gotowe - Spacjomat startuje (ikona przy zegarku pojawi sie za kilka sekund).
timeout /t 4 /nobreak >nul
