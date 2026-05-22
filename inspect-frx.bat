@echo off
setlocal

set "SCRIPT_DIR=%~dp0"

if "%~1"=="" (
    echo Usage:
    echo   inspect-frx.bat UserForm1.frx [output.json]
    echo   inspect-frx.bat UserForm1.frm [output.json]
    exit /b 2
)

set "INPUT=%~f1"
set "OUT=%~2"

if /I "%~x1"==".frx" (
    set "FORM=%~dpn1.frm"
) else if /I "%~x1"==".frm" (
    set "FORM=%INPUT%"
) else (
    echo ERROR: expected a .frx or .frm file, got "%~1".
    exit /b 2
)

if not exist "%FORM%" (
    echo ERROR: matching .frm file was not found:
    echo   %FORM%
    echo.
    echo The inspector needs the .frm next to the .frx because the UserForm metadata lives in the pair.
    exit /b 1
)

if "%OUT%"=="" (
    set "OUT=%~dpn1.inspect.json"
) else (
    set "OUT=%~f2"
)

for %%I in ("%OUT%") do set "RAW_OUT=%%~dpnI.raw.json"
for %%I in ("%OUT%") do set "PATCH_OUT=%%~dpnI.patch.json"

pushd "%SCRIPT_DIR%" >nul
dotnet run --project "src\FrxEdit.Cli\FrxEdit.Cli.csproj" -- inspect "%FORM%" --mode strict --out "%OUT%" --raw-out "%RAW_OUT%"
set "EXIT_CODE=%ERRORLEVEL%"
if %EXIT_CODE% EQU 0 (
    dotnet run --project "src\FrxEdit.Cli\FrxEdit.Cli.csproj" -- inspect "%FORM%" --mode strict --out "%PATCH_OUT%" --as-patch
    set "EXIT_CODE=%ERRORLEVEL%"
)
popd >nul

exit /b %EXIT_CODE%
