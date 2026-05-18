$signature = @'
[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool AttachConsole(uint dwProcessId);

[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool FreeConsole();

[DllImport("kernel32.dll")]
public static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);
'@

$win32 = Add-Type -MemberDefinition $signature -Name Win32 -Namespace OneHealth -PassThru

function Send-CtrlC($proc) {
    if ($null -eq $proc -or $proc.HasExited) { return }
    $win32::FreeConsole()                          | Out-Null
    $win32::AttachConsole($proc.Id)                | Out-Null
    $win32::GenerateConsoleCtrlEvent(0, 0)         | Out-Null  # CTRL_C_EVENT
    $win32::FreeConsole()                          | Out-Null
    $proc.WaitForExit(3000)                        | Out-Null
}

$titles = @("Sensor_001", "Gateway_001", "Server", "ServicoAnalise", "PreProcessamento", "Frontend")

Write-Host "[ONE HEALTH] A encerrar servicos graciosamente..."

foreach ($title in $titles) {
    $procs = Get-Process | Where-Object { $_.MainWindowTitle -like "*$title*" }
    foreach ($proc in $procs) {
        Write-Host "  -> A encerrar: $title (PID $($proc.Id))"
        Send-CtrlC $proc
        if (-not $proc.HasExited) {
            Write-Host "     Forcando encerramento de $title..."
            $proc.Kill()
        }
    }
}

Write-Host "[ONE HEALTH] Sistema encerrado."
