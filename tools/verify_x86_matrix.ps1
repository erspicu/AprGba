# tools/verify_x86_matrix.ps1 — 8086 6-demo × 3-backend screenshot matrix.
#
# Runs all six 24.5/24.3 hand-crafted .com demos through legacy /
# json / json-block backends, then SHA256-compares the resulting
# CGA-render PNGs. Pass = all 18 PNGs are pixel-identical (3 backends
# × 6 demos all agree).
#
# Used as the Tier-2 visual regression check after any change touching
# X86 emitters, x86 spec JSON, BlockFunctionBuilder, or X86JsonCpu.
#
# Usage: pwsh tools/verify_x86_matrix.ps1
#   (must be run from repo root; expects Release build at
#    src/AprX86.Cli/bin/Release/net10.0/apr-x86.dll — run
#    `dotnet build -c Release src/AprX86.Cli/AprX86.Cli.csproj` first)

$dll = "src/AprX86.Cli/bin/Release/net10.0/apr-x86.dll"
$demos = @(
    @{ Name="hello-cga";   Rom="test-roms/x86/24.3-hello-cga.com";    Cycles=200000   },
    @{ Name="primes";      Rom="test-roms/x86/24.5-primes.com";       Cycles=20000000 },
    @{ Name="fibonacci";   Rom="test-roms/x86/24.5-fibonacci.com";    Cycles=200000   },
    @{ Name="mandelbrot";  Rom="test-roms/x86/24.5-mandelbrot.com";   Cycles=80000000 },
    @{ Name="string-copy"; Rom="test-roms/x86/24.5-string-copy.com";  Cycles=200000   },
    @{ Name="factorial";   Rom="test-roms/x86/24.5-factorial.com";    Cycles=200000   }
)
$backends = @("legacy", "json", "json-block")

$results = @()
$outDir = "temp/t2-x86"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

foreach ($demo in $demos) {
    $hashes = @{}
    foreach ($be in $backends) {
        $png = "$outDir/$($demo.Name)-$be.png"
        $log = "$outDir/$($demo.Name)-$be.log"
        & dotnet $dll --rom=$($demo.Rom) --backend=$be --max-cycles=$($demo.Cycles) --screenshot=$png > $log 2>&1
        if (-not (Test-Path $png)) {
            $hashes[$be] = "MISSING"
        } else {
            $hashes[$be] = (Get-FileHash -Algorithm SHA256 $png).Hash.Substring(0, 12)
        }
    }
    $allSame = ($hashes["legacy"] -eq $hashes["json"]) -and ($hashes["legacy"] -eq $hashes["json-block"])
    $verdict = if ($allSame) { "OK   " } else { "FAIL " }
    Write-Output ("$verdict {0,-12} legacy={1} json={2} json-block={3}" -f $demo.Name, $hashes["legacy"], $hashes["json"], $hashes["json-block"])
    $results += [pscustomobject]@{
        Demo       = $demo.Name
        Legacy     = $hashes["legacy"]
        Json       = $hashes["json"]
        JsonBlock  = $hashes["json-block"]
        AllMatch   = $allSame
    }
}

Write-Output ""
$failed = ($results | Where-Object { -not $_.AllMatch }).Count
if ($failed -eq 0) {
    Write-Output "ALL 6 DEMOS x 3 BACKENDS = 18 SCREENSHOTS PIXEL-IDENTICAL"
    exit 0
} else {
    Write-Output "FAILED: $failed demo(s) have backend mismatch"
    exit 1
}
