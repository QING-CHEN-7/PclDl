@echo off
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
if errorlevel 1 exit /b 1
cd /d "%~dp0PclDl.Bridge"
if not exist bin mkdir bin
cl /nologo /clr /LD /O2 /DUNICODE /D_UNICODE PclDl.Bridge.cpp /link /OUT:bin\PclDl.Bridge.dll
exit /b %errorlevel%
