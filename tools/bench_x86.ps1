# tools/bench_x86.ps1 — benchmark Intel 8086 backends.
#
# Generates a tight loop ROM (~20M instructions) and runs each backend
# 3 times wall-clock, reporting min/avg latency and best-case MIPS.
#
# Usage: pwsh tools/bench_x86.ps1
#   (must be run from repo root; expects Release build at
#    src/AprX86.Cli/bin/Release/net10.0/apr-x86.dll)

# --- Generate bench-loop.com ---
# 8086 assembly:
#   0x100:  mov bx, 100         (outer counter)
#   0x103:  mov cx, 0xFFFF      (inner counter)
#   0x106:  add ax, 1
#   0x109:  add ax, 2
#   0x10C:  loop -8             (target = 0x106)
#   0x10E:  dec bx
#   0x10F:  jnz -14             (target = 0x103)
#   0x111:  hlt
#
# Per outer iter: 1 (mov cx) + 65535 * 3 (add/add/loop) + 1 (dec) +
#                 1 (jnz) = 196,610 instructions.
# Outer iters: 100 → 19.66M instructions total. Wall-clock ~1-30s
# depending on backend.

$romBytes = [byte[]]@(
    0xBB, 0x64, 0x00,    # mov bx, 100
    0xB9, 0xFF, 0xFF,    # mov cx, 0xFFFF
    0x05, 0x01, 0x00,    # add ax, 1
    0x05, 0x02, 0x00,    # add ax, 2
    0xE2, 0xF8,          # loop -8
    0x4B,                # dec bx
    0x75, 0xF2,          # jnz -14
    0xF4                 # hlt
)
$romPath = "test-roms/x86/bench-loop.com"
[System.IO.File]::WriteAllBytes($romPath, $romBytes)
Write-Output "Generated $romPath ($($romBytes.Length) bytes)"
Write-Output ""

# --- Run benchmark ---
$dll = "src/AprX86.Cli/bin/Release/net10.0/apr-x86.dll"
if (-not (Test-Path $dll)) {
    Write-Error "Release build not found at $dll. Run: dotnet build -c Release src/AprX86.Cli/AprX86.Cli.csproj"
    exit 1
}

$backends = @("legacy", "json", "json-block")
$results  = @()

# Workload truth: known by construction.
#   1 (mov bx) + 100 outer × (1 mov cx + 65535 × 3 [add/add/loop] + 1 dec + 1 jnz)
#   = 1 + 100 × (1 + 196605 + 1 + 1) + 1 hlt
#   = 1 + 100 × 196608 + 1 = 19,660,802 architectural instructions.
$workloadInstrs = 19660802

# max-cycles must be high enough that legacy (which counts true cycles per
# instruction, ~5-8 per opcode) reaches HLT. 200M cycles is safe.
$maxCycles = 200000000

foreach ($backend in $backends) {
    Write-Output "=== $backend ==="
    $times = @()
    $stepsLast = 0
    $haltedLast = $false
    for ($i = 1; $i -le 3; $i++) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $out = & dotnet $dll --rom=$romPath --backend=$backend --max-cycles=$maxCycles 2>&1
        $sw.Stop()
        $ms = $sw.Elapsed.TotalMilliseconds
        $times += $ms
        $outStr = $out -join "`n"
        if ($outStr -match "ran ([\d,]+) instr") {
            $stepsLast = [int64]($matches[1] -replace ",", "")
        }
        if ($outStr -match "halted: True") { $haltedLast = $true }
        Write-Output ("  run {0}: {1,7:F0} ms  steps={2:N0}  halted={3}" -f $i, $ms, $stepsLast, $haltedLast)
    }
    $avg = [math]::Round(($times | Measure-Object -Average).Average, 0)
    $min = [math]::Round(($times | Measure-Object -Minimum).Minimum, 0)
    if ($haltedLast) {
        # Fair MIPS: total architectural instructions / wall-clock (best run).
        $mips = [math]::Round($workloadInstrs / 1000.0 / $min, 2)
        Write-Output ("  → avg={0,5} ms  min={1,5} ms  workload MIPS={2,6:F2}" -f $avg, $min, $mips)
    } else {
        Write-Output ("  → avg={0,5} ms  min={1,5} ms  did NOT complete workload (cycle cap?)" -f $avg, $min)
        $mips = 0
    }
    Write-Output ""
    $results += [pscustomobject]@{
        Backend = $backend; AvgMs = $avg; MinMs = $min; MIPS = $mips; Halted = $haltedLast
    }
}

Write-Output "--- Summary (includes .NET + LLVM startup) ---"
$results | Format-Table Backend,AvgMs,MinMs,MIPS,Halted -AutoSize

# --- Baseline: 1-byte HLT-only ROM to measure pure startup overhead ---
Write-Output ""
Write-Output "=== Startup baseline (1-byte HLT ROM) ==="
$baselinePath = "temp/baseline-hlt.com"
if (-not (Test-Path $baselinePath)) {
    [System.IO.File]::WriteAllBytes($baselinePath, [byte[]]@(0xF4))
}
$baselines = @{}
foreach ($backend in $backends) {
    $bms = @()
    for ($i = 1; $i -le 3; $i++) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $null = & dotnet $dll --rom=$baselinePath --backend=$backend --max-cycles=10 2>&1
        $sw.Stop()
        $bms += $sw.Elapsed.TotalMilliseconds
    }
    $baselines[$backend] = [math]::Round(($bms | Measure-Object -Minimum).Minimum, 0)
    Write-Output ("  {0,-12} startup baseline: {1,5} ms (best of 3)" -f $backend, $baselines[$backend])
}

# --- Net MIPS (workload time minus startup baseline) ---
Write-Output ""
Write-Output "--- Net MIPS (excluding startup) ---"
$netResults = @()
foreach ($r in $results) {
    if (-not $r.Halted) { continue }
    $netMs = $r.MinMs - $baselines[$r.Backend]
    if ($netMs -le 0) { $netMs = 1 }
    $netMips = [math]::Round($workloadInstrs / 1000.0 / $netMs, 2)
    $netResults += [pscustomobject]@{
        Backend     = $r.Backend
        WallMs      = $r.MinMs
        StartupMs   = $baselines[$r.Backend]
        NetMs       = $netMs
        NetMIPS     = $netMips
    }
}
$netResults | Format-Table -AutoSize

Write-Output ""
Write-Output "Workload: $($workloadInstrs.ToString('N0')) architectural instructions"
Write-Output "  outer loop x100, inner add/add/loop x65535 per outer."
