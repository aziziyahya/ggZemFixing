# ==============================================================================
#  ggZemFixing - Problem Simulation & Demo Utility
#  Safely simulates real XAMPP failures to demo ggZemFixing in action.
# ==============================================================================

$Host.UI.RawUI.WindowTitle = "ggZemFixing - Demo and Problem Simulation Tool"

function Detect-Xampp {
    $candidates = @("C:\src\xampp", "C:\xampp", "D:\xampp", "E:\xampp")
    foreach ($c in $candidates) {
        if ((Test-Path "$c\mysql\backup") -and (Test-Path "$c\mysql\data")) {
            return $c
        }
    }
    return $null
}

$XamppPath = Detect-Xampp
if (-not $XamppPath) {
    $XamppPath = "C:\src\xampp"
}

function Show-Header {
    Clear-Host
    Write-Host "======================================================================" -ForegroundColor Cyan
    Write-Host "             ggZemFixing - Problem Simulation & Demo Tool             " -ForegroundColor Yellow
    Write-Host "======================================================================" -ForegroundColor Cyan
    Write-Host " Active XAMPP Path: " -NoNewline
    if (Test-Path $XamppPath) {
        Write-Host "$XamppPath " -ForegroundColor Green -NoNewline
        Write-Host "[Detected]" -ForegroundColor DarkGreen
    } else {
        Write-Host "$XamppPath " -ForegroundColor Red -NoNewline
        Write-Host "[Not Found]" -ForegroundColor DarkRed
    }
    Write-Host "----------------------------------------------------------------------" -ForegroundColor DarkGray
}

function Simulate-MySqlCrash {
    Show-Header
    Write-Host "`n[SIMULATION 1] Corrupt MySQL Transaction Log (Classic InnoDB Crash)`n" -ForegroundColor Yellow
    Write-Host "This will simulate the most common XAMPP MySQL failure:" -ForegroundColor White
    Write-Host "  * Injects mock corruption into 'ib_logfile0' (mimicking a power cut / bluescreen crash)." -ForegroundColor Gray
    Write-Host "  * Makes MySQL fail to start in XAMPP with: 'Error: MySQL shutdown unexpectedly'." -ForegroundColor Gray
    Write-Host "  * Demonstrates how ggZemFixing safely repairs the tables without losing data.`n" -ForegroundColor Gray

    $mysqlData = "$XamppPath\mysql\data"
    $logFile   = "$mysqlData\ib_logfile0"
    $backupLog = "$mysqlData\ib_logfile0.sim_backup"

    if (-not (Test-Path $logFile)) {
        Write-Host "[ERROR] Could not find '$logFile'." -ForegroundColor Red
        Pause
        return
    }

    # 1. Stop mysqld if currently running
    $running = Get-Process -Name "mysqld" -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "Stopping running MySQL instance (PID $($running.Id))..." -ForegroundColor Yellow
        Stop-Process -Name "mysqld" -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
    }

    # 2. Backup original ib_logfile0
    if (-not (Test-Path $backupLog)) {
        Write-Host "Creating safe backup: ib_logfile0.sim_backup..." -ForegroundColor Cyan
        Copy-Item -Path $logFile -Destination $backupLog -Force
    }

    # 3. Corrupt the file header with random garbage bytes
    Write-Host "Injecting corruption into ib_logfile0..." -ForegroundColor Magenta
    $bytes = New-Object byte[] 65536
    (New-Object System.Random).NextBytes($bytes)
    [System.IO.File]::WriteAllBytes($logFile, $bytes)

    Write-Host "`n========================= DEMO INSTRUCTIONS =========================" -ForegroundColor Green
    Write-Host "1. Open XAMPP Control Panel and click [Start] next to MySQL." -ForegroundColor White
    Write-Host "   -> NOTICE: MySQL crashes immediately with: " -NoNewline -ForegroundColor Yellow
    Write-Host "'MySQL shutdown unexpectedly'!" -ForegroundColor Red
    Write-Host "2. Open ggZemFixing." -ForegroundColor White
    Write-Host "   -> Notice XAMPP path '$XamppPath' is recognized automatically." -ForegroundColor White
    Write-Host "3. Click [Start Safe Recovery]." -ForegroundColor White
    Write-Host "   -> ggZemFixing backs up the broken data, restores clean engine templates," -ForegroundColor Gray
    Write-Host "      and reconnects all your custom databases intact." -ForegroundColor Gray
    Write-Host "4. Go back to XAMPP Control Panel and click [Start] on MySQL." -ForegroundColor White
    Write-Host "   -> MySQL turns GREEN and starts perfectly!" -ForegroundColor Green
    Write-Host "======================================================================" -ForegroundColor Green

    Write-Host "`nNote: A safe backup is saved at: $backupLog" -ForegroundColor DarkGray
    Write-Host "You can also choose [3] Restore from this menu at any time.`n" -ForegroundColor DarkGray
    Pause
}

function Simulate-PortBlock {
    Show-Header
    Write-Host "`n[SIMULATION 2] Block Port 80 (Simulate Apache Port Conflict)`n" -ForegroundColor Yellow
    Write-Host "This launches a background TCP listener on Port 80." -ForegroundColor White
    Write-Host "  * When you click [Start] on Apache in XAMPP, it will fail because Port 80 is occupied." -ForegroundColor Gray
    Write-Host "  * In ggZemFixing, click [Inspect Ports & Windows Services] to see it detect the conflict in RED.`n" -ForegroundColor Gray

    try {
        $endpoint = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 80)
        $listener = New-Object System.Net.Sockets.TcpListener($endpoint)
        $listener.Start()
        Write-Host "[SUCCESS] Port 80 is now BLOCKED by this test script!" -ForegroundColor Green
    } catch {
        Write-Host "[ERROR] Could not bind Port 80. Port might already be in use or requires Administrator rights." -ForegroundColor Red
        Write-Host "Error details: $($_.Exception.Message)" -ForegroundColor DarkRed
        Pause
        return
    }

    Write-Host "`n========================= DEMO INSTRUCTIONS =========================" -ForegroundColor Green
    Write-Host "1. Try to start Apache in XAMPP Control Panel." -ForegroundColor White
    Write-Host "   -> Notice it fails because Port 80 is locked." -ForegroundColor Red
    Write-Host "2. In ggZemFixing, click [Inspect Ports & Windows Services]." -ForegroundColor White
    Write-Host "   -> Notice Port 80 turns RED and shows this listener process!" -ForegroundColor Yellow
    Write-Host "3. Press any key in this window to release Port 80 and stop the listener." -ForegroundColor White
    Write-Host "======================================================================`n" -ForegroundColor Green

    Write-Host "Listener is currently running. Press any key to stop it..." -ForegroundColor Cyan
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    $listener.Stop()
    Write-Host "`n[RELEASED] Port 80 has been freed!" -ForegroundColor Green
    Start-Sleep -Seconds 1
}

function Restore-MySql {
    Show-Header
    Write-Host "`n[RESTORE] Revert MySQL to Original State`n" -ForegroundColor Yellow

    $mysqlData = "$XamppPath\mysql\data"
    $logFile   = "$mysqlData\ib_logfile0"
    $backupLog = "$mysqlData\ib_logfile0.sim_backup"

    if (Test-Path $backupLog) {
        $running = Get-Process -Name "mysqld" -ErrorAction SilentlyContinue
        if ($running) {
            Write-Host "Stopping running MySQL instance..." -ForegroundColor Yellow
            Stop-Process -Name "mysqld" -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 800
        }

        Copy-Item -Path $backupLog -Destination $logFile -Force
        Remove-Item -Path $backupLog -Force -ErrorAction SilentlyContinue
        Write-Host "[SUCCESS] Original 'ib_logfile0' restored from backup!" -ForegroundColor Green
    } else {
        Write-Host "[INFO] No '.sim_backup' file found. MySQL is already clean or was recovered by ggZemFixing." -ForegroundColor Cyan
    }
    Write-Host ""
    Pause
}

function Change-XamppPath {
    Show-Header
    Write-Host "`nCurrent Path: $XamppPath" -ForegroundColor Cyan
    $newPath = Read-Host "Enter new XAMPP installation directory (e.g. C:\src\xampp)"
    if (-not [string]::IsNullOrWhiteSpace($newPath)) {
        $script:XamppPath = $newPath.Trim().TrimEnd('\')
    }
}

# Main Menu Loop
do {
    Show-Header
    Write-Host "Select an option to simulate or restore:" -ForegroundColor White
    Write-Host ""
    Write-Host " [1] " -NoNewline -ForegroundColor Green
    Write-Host "Simulate Real MySQL Crash (Corrupt ib_logfile0 - Safe & Reversible)" -ForegroundColor White
    Write-Host " [2] " -NoNewline -ForegroundColor Green
    Write-Host "Simulate Apache Port 80 Conflict (Locks Port 80 to test Inspector)" -ForegroundColor White
    Write-Host " [3] " -NoNewline -ForegroundColor Cyan
    Write-Host "Restore MySQL from Backup (Revert simulation)" -ForegroundColor White
    Write-Host " [4] " -NoNewline -ForegroundColor Yellow
    Write-Host "Change XAMPP Directory (Current: $XamppPath)" -ForegroundColor White
    Write-Host " [5] " -NoNewline -ForegroundColor Red
    Write-Host "Exit" -ForegroundColor White
    Write-Host ""
    $choice = Read-Host "Enter choice [1-5]"

    switch ($choice) {
        "1" { Simulate-MySqlCrash }
        "2" { Simulate-PortBlock }
        "3" { Restore-MySql }
        "4" { Change-XamppPath }
        "5" { exit }
    }
} while ($true)
