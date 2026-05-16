@echo off
REM ============================================================================
REM gui-test.bat -- launch AprPc in WinForms GUI mode for interactive testing
REM
REM Usage:
REM   gui-test.bat                 HLE BIOS + FreeDOS (per-instruction backend,
REM                                because HLE + block-JIT hits Phase 28.8x)
REM   gui-test.bat realbios        real pcxtbios.bin + videorom.bin + FreeDOS
REM                                (VGA mode 3, recommended). 2nd arg = mda|cga|vga
REM                                (default vga); 3rd arg = "auto" for AutoTester.
REM   gui-test.bat hle-jit         HLE BIOS + block-JIT  (known broken; for
REM                                reproducing Phase 28.8x)
REM   gui-test.bat build           force rebuild before launching (HLE mode)
REM
REM Always run from the repo root or by double-clicking -- the script cd's to
REM its own directory so BIOS\... paths resolve correctly.
REM ============================================================================

cd /d "%~dp0"

set MODE=%1
if "%MODE%"=="" set MODE=hle

if /i "%MODE%"=="build"        goto :do_build
if /i "%MODE%"=="hle"          goto :run_hle
if /i "%MODE%"=="hle-perinstr" goto :run_hle
if /i "%MODE%"=="hle-jit"      goto :run_hle_jit
if /i "%MODE%"=="realbios"     goto :run_realbios

echo [gui-test] Unknown mode: %MODE%
echo [gui-test] Valid modes: hle ^| hle-jit ^| realbios ^| build
exit /b 2

:do_build
echo [gui-test] Rebuilding AprPc.Cli ...
dotnet build src\AprPc.Cli\AprPc.Cli.csproj --no-incremental
if errorlevel 1 (
    echo [gui-test] Build failed.
    exit /b 1
)
set MODE=hle
goto :run_hle

:ensure_dll
set DLL=src\AprPc.Cli\bin\Debug\net10.0-windows\apr-pc.dll
if exist "%DLL%" goto :eof
echo [gui-test] %DLL% not found. Building first ...
dotnet build src\AprPc.Cli\AprPc.Cli.csproj --no-incremental
if errorlevel 1 (
    echo [gui-test] Build failed.
    exit /b 1
)
goto :eof

:run_hle
call :ensure_dll
set FLOPPY=BIOS\freedos-1.3-floppy.img
if not exist "%FLOPPY%" (
    echo [gui-test] Missing %FLOPPY%.
    exit /b 1
)
echo [gui-test] Mode: HLE BIOS + FreeDOS  (backend=json, per-instruction)
dotnet "%DLL%" --floppy-a=%FLOPPY% --backend=json --window-scale=2 --window-title="AprPc - HLE BIOS - per-instr" --verbose
goto :eof

:run_hle_jit
call :ensure_dll
set FLOPPY=BIOS\freedos-1.3-floppy.img
if not exist "%FLOPPY%" (
    echo [gui-test] Missing %FLOPPY%.
    exit /b 1
)
echo [gui-test] Mode: HLE BIOS + FreeDOS  (backend=json-block, KNOWN BROKEN -- Phase 28.8x)
dotnet "%DLL%" --floppy-a=%FLOPPY% --window-scale=2 --window-title="AprPc - HLE BIOS - JIT (broken)" --verbose
goto :eof

:run_realbios
call :ensure_dll
set FLOPPY=BIOS\freedos-1.3-floppy.img
set BIOS=BIOS\firmware\pcxtbios.bin
REM Second positional arg picks video adapter:
REM   mda  = pcxtbios MDA path only (monochrome, attr-quirks)
REM   cga  = pcxtbios CGA path only
REM   vga  = pcxtbios + load videorom.bin (Tseng ET4000 VGA BIOS) at 0xC0000.
REM          POST FAR-CALLs its init; the VGA BIOS forces mode 3 (CGA color)
REM          and everything renders properly. Phase 30.12, recommended default.
set VIDEO=%2
if "%VIDEO%"=="" set VIDEO=vga
set VBIOS_ARG=
if /i "%VIDEO%"=="vga" set VBIOS_ARG=--video-bios=BIOS\firmware\videorom.bin
if not exist "%FLOPPY%" (
    echo [gui-test] Missing %FLOPPY%.
    exit /b 1
)
if not exist "%BIOS%" (
    echo [gui-test] Missing %BIOS%.
    exit /b 1
)
REM block-JIT + Phase 29 FPU still broken (AllocaSlotProvider 64-bit slot
REM layout mismatch); pcxtbios.bin POST runs FNINIT so we hit this.
REM Force per-instruction backend until block-JIT FPU is fixed.
REM PIT at default 18Hz -- 200Hz caused FreeDOS time computation hangs
REM (BDA tick counter advances 11x faster than wall clock, hits some
REM FreeDOS internal conversion corner case). The FDC motor-on hack
REM + 2ms HLT-wake polling deliver enough responsiveness on their own.
REM Third positional arg = "auto" to run scripted bring-up test.
set AUTO=%3
set AUTO_ARG=
if /i "%AUTO%"=="auto" set AUTO_ARG=--auto-test=freedos-mda-dir
echo [gui-test] Mode: Real BIOS pcxtbios.bin + FreeDOS  (Phase 30 path, backend=json, video=%VIDEO%, auto=%AUTO%)
REM --video=vga also passes through the MDA renderer codepath as a fallback;
REM whichever framebuffer (0xB0000 or 0xB8000) the VBIOS init populates wins.
set VIDEO_RENDER=%VIDEO%
if /i "%VIDEO%"=="vga" set VIDEO_RENDER=cga
dotnet "%DLL%" --bios=%BIOS% %VBIOS_ARG% --floppy-a=%FLOPPY% --backend=json --video=%VIDEO_RENDER% %AUTO_ARG% --window-scale=2 --window-title="AprPc - real BIOS pcxtbios.bin (%VIDEO%)" --verbose
goto :eof
