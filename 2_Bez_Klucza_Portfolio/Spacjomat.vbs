Set FSO = CreateObject("Scripting.FileSystemObject")
Set WshShell = CreateObject("WScript.Shell")
AppDir = FSO.GetParentFolderName(WScript.ScriptFullName)
WshShell.CurrentDirectory = AppDir

' Lekki nadzorca wscript (~3 MB RAM): uruchamia Spacjomat.ps1 -Worker i automatycznie wznawia po nieoczekiwanej awarii.
' Kod wyjscia 99 = celowe zamkniecie ("Zakoncz Spacjomat" / "Restart") lub wykryta juz dzialajaca kopia.
Dim cmd, rc, crashes
cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & AppDir & "\Spacjomat.ps1"" -Worker"
crashes = 0

Do
    rc = WshShell.Run(cmd, 0, True)
    If rc = 99 Then Exit Do
    crashes = crashes + 1
    If crashes > 4 Then
        WScript.Sleep 10000
    Else
        WScript.Sleep 2000
    End If
Loop
