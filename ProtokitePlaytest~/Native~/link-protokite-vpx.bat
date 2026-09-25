@echo off
rem Called by build-protokite-vpx.sh, with the Visual Studio 2022 install folder vswhere found:
rem   link-protokite-vpx.bat <Visual Studio folder> <libvpx source folder> <libvpx build folder> <output folder>
rem   link-protokite-vpx.bat <Visual Studio folder> --inspect <dll>      prints the DLL's exports and the DLLs it loads
setlocal
call "%~1\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
if "%~2"=="--inspect" (
  dumpbin /nologo /exports "%~3" || exit /b 1
  dumpbin /nologo /dependents "%~3" || exit /b 1
  exit /b 0
)
if not exist "%~4" mkdir "%~4"
cl /nologo /O2 /MT /W4 /WX /LD "%~dp0protokite_vpx.c" /I"%~2" /I"%~3" /Fo:"%~4\\" /Fe:"%~4\protokite_vpx.dll" /link "%~3\x64\Release\vpxmt.lib" || exit /b 1
exit /b 0
