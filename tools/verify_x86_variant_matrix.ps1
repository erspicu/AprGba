# tools/verify_x86_variant_matrix.ps1 — Phase 25 inheritance proof.
#
# Runs the existing 6 i8086 demos through both --variant=i8086 and
# --variant=i80186 on json-block backend, SHA256-compares each pair.
# Pass = all 6 pairs identical = i80186 spec inherits i8086 cleanly
# (no demo uses any i80186-specific opcode, so behavior must match
# byte-for-byte if inheritance + override resolution is correct).
$dll = "src/AprX86.Cli/bin/Release/net10.0/apr-x86.dll"
$demos = @(
    @{ Name="hello-cga";   Rom="test-roms/x86/24.3-hello-cga.com";    Cycles=200000   },
    @{ Name="primes";      Rom="test-roms/x86/24.5-primes.com";       Cycles=20000000 },
    @{ Name="fibonacci";   Rom="test-roms/x86/24.5-fibonacci.com";    Cycles=200000   },
    @{ Name="mandelbrot";  Rom="test-roms/x86/24.5-mandelbrot.com";   Cycles=80000000 },
    @{ Name="string-copy"; Rom="test-roms/x86/24.5-string-copy.com";  Cycles=200000   },
    @{ Name="factorial";   Rom="test-roms/x86/24.5-factorial.com";    Cycles=200000   }
)
$outDir = "temp/p25.5-matrix"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$allOk = $true
foreach ($demo in $demos) {
    $hashes = @{}
    foreach ($variant in @("i8086", "i80186")) {
        $png = "$outDir/$($demo.Name)-$variant.png"
        & dotnet $dll --rom=$($demo.Rom) --backend=json-block --variant=$variant --max-cycles=$($demo.Cycles) --screenshot=$png 2>&1 | Out-Null
        $hashes[$variant] = if (Test-Path $png) { (Get-FileHash -Algorithm SHA256 $png).Hash.Substring(0, 12) } else { "MISSING" }
    }
    $match = $hashes["i8086"] -eq $hashes["i80186"]
    if (-not $match) { $allOk = $false }
    $verdict = if ($match) { "OK   " } else { "FAIL " }
    Write-Output ("$verdict {0,-12} i8086={1} i80186={2}" -f $demo.Name, $hashes["i8086"], $hashes["i80186"])
}
if ($allOk) { Write-Output ""; Write-Output "ALL 6 DEMOS PIXEL-IDENTICAL ACROSS i8086/i80186 (json-block)" }
