$ErrorActionPreference = 'Stop'
$target = $env:LAYERCAD_UPDATE_TARGET
$folder = $env:LAYERCAD_UPDATE_FOLDER
$payload = Join-Path $folder 'update.exe'
$staged = "$target.update-$([guid]::NewGuid())"
$backup = "$target.previous"
try {
    if ((Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash -ne $env:LAYERCAD_UPDATE_HASH) {
        throw 'The download failed SHA-256 verification.'
    }
    $app = Get-Process -Id ([int]$env:LAYERCAD_UPDATE_PID) -ErrorAction SilentlyContinue
    if ($app -and !$app.WaitForExit(300000)) { throw 'LayerCAD did not close.' }
    Copy-Item -LiteralPath $payload -Destination $staged
    [IO.File]::Replace($staged, $target, $backup)
    try {
        $launch = [Diagnostics.ProcessStartInfo]::new($target)
        $launch.UseShellExecute = $false
        [Diagnostics.Process]::Start($launch) | Out-Null
    } catch {
        [IO.File]::Replace($backup, $target, $null)
        throw
    }
} catch {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show("LayerCAD could not finish the update. $($_.Exception.Message)", 'LayerCAD Update') | Out-Null
} finally {
    Remove-Item -LiteralPath $staged,$payload -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $folder -ErrorAction SilentlyContinue
}
