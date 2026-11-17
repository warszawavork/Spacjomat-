param([switch]$Worker)

# Spacjomat - Hybrydowa Autokorekta (AI + Offline 4.5M SJP + GBoard + LanguageTool)
$dir = $PSScriptRoot
if ([string]::IsNullOrEmpty($dir)) {
    $dir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Set-Location $dir
[System.IO.Directory]::SetCurrentDirectory($dir)
$env:SPACJOMAT_DIR = $dir
$logFile = Join-Path $dir "log.txt"

function Write-SafeLog([string]$msg) {
    try {
        Add-Content -Path $logFile -Value ("[" + (Get-Date -Format "HH:mm:ss") + "] " + $msg) -Encoding UTF8 -ErrorAction SilentlyContinue
    } catch { }
}

# Szybkie sprawdzenie czy Spacjomat juz dziala przed kompilacja C# (oszczedza CPU i RAM)
$mutexExists = $false
try {
    $m = [System.Threading.Mutex]::OpenExisting("Spacjomat_Live_v12_Default")
    $m.Close()
    $mutexExists = $true
} catch [System.UnauthorizedAccessException] {
    $mutexExists = $true
} catch { }

if ($mutexExists) {
    try {
        $ev = [System.Threading.EventWaitHandle]::OpenExisting("Spacjomat_Ping_v12_Default")
        $ev.Set() | Out-Null
        $ev.Close()
    } catch { }
    exit 99
}

try {
    $targetTag = "Spacjomat" + ".ps1"
    $oldProcs = Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessId -ne $PID -and $_.CommandLine -like ("*-File*" + $targetTag + "*")
    }
    foreach ($op in $oldProcs) {
        try { Stop-Process -Id $op.ProcessId -Force -ErrorAction SilentlyContinue } catch { }
    }
    if ($oldProcs) { Start-Sleep -Milliseconds 350 }

    $code = [System.IO.File]::ReadAllText((Join-Path $dir "Spacjomat.cs"), [System.Text.Encoding]::UTF8)
    Add-Type -TypeDefinition $code -ReferencedAssemblies System.Windows.Forms, System.Drawing, System.Web.Extensions
    $rc = [SpacjomatContext]::Main()
    if ($rc -ne 99) {
        Write-SafeLog ("Worker zakonczyl sie z kodem=" + $rc + " — wscript nadzorca uruchomi go ponownie.")
    }
    exit $rc
} catch {
    $errMsg = "Blad krytyczny startu Spacjomatu: " + $_.Exception.ToString()
    Write-SafeLog $errMsg
    exit 1
}
