# Spacjomat - Hybrydowa Autokorekta (AI + Offline 4.5M SJP + GBoard + LanguageTool)
$dir = $PSScriptRoot
if ([string]::IsNullOrEmpty($dir)) {
    $dir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Set-Location $dir
[System.IO.Directory]::SetCurrentDirectory($dir)
$env:SPACJOMAT_DIR = $dir
$logFile = Join-Path $dir "log.txt"

try {
    # Jesli nie dziala jeszcze v12 na biezacym pulpicie, zamknij ewentualna starsza wersje Spacjomatu
    $v12Exists = $false
    try {
        $m = [System.Threading.Mutex]::OpenExisting("Spacjomat_Live_v12_Default")
        $m.Close()
        $v12Exists = $true
    } catch { }

    if (-not $v12Exists) {
        $oldProcs = Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" -ErrorAction SilentlyContinue | Where-Object {
            $_.ProcessId -ne $PID -and $_.CommandLine -like "*Spacjomat.ps1*"
        }
        foreach ($op in $oldProcs) {
            try { Stop-Process -Id $op.ProcessId -Force -ErrorAction SilentlyContinue } catch { }
        }
        if ($oldProcs) { Start-Sleep -Milliseconds 400 }
    }

    $code = [System.IO.File]::ReadAllText((Join-Path $dir "Spacjomat.cs"), [System.Text.Encoding]::UTF8)
    Add-Type -TypeDefinition $code -ReferencedAssemblies System.Windows.Forms, System.Drawing, System.Web.Extensions
    [SpacjomatContext]::Main()
} catch {
    $errMsg = "Blad krytyczny startu Spacjomatu: " + $_.Exception.ToString()
    Add-Content -Path $logFile -Value ("[" + (Get-Date -Format "HH:mm:ss") + "] " + $errMsg) -Encoding UTF8
    [System.Windows.Forms.MessageBox]::Show($errMsg, "Spacjomat", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error)
}
