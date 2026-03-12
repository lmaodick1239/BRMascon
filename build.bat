@echo off
echo ============================================
echo BRMascon Build Script
echo ============================================
echo.

REM Clean previous builds
echo Cleaning previous builds...
if exist "publish" rmdir /s /q "publish"
mkdir "publish"

REM Build Framework-Dependent Version (requires .NET 9.0 installed)
echo.
echo Building Framework-Dependent version...
echo (Requires .NET 9.0 Runtime to be installed on target machine)
echo.
dotnet publish -c Release -o "publish\BRMascon-FrameworkDependent" --self-contained false

if %errorlevel% neq 0 (
    echo.
    echo ERROR: Framework-Dependent build failed!
    pause
    exit /b %errorlevel%
)

REM Build Self-Contained Standalone Version (Windows x64)
echo.
echo Building Self-Contained Standalone version (Windows x64)...
echo (Includes .NET Runtime - no installation required)
echo.
dotnet publish -c Release -r win-x64 -o "publish\BRMascon-Standalone" --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true

if %errorlevel% neq 0 (
    echo.
    echo ERROR: Standalone build failed!
    pause
    exit /b %errorlevel%
)

echo.
echo ============================================
echo Build Complete!
echo ============================================
echo.
echo Framework-Dependent: publish\BRMascon-FrameworkDependent\BRMascon.exe
echo Standalone:          publish\BRMascon-Standalone\BRMascon.exe
echo.
echo The Standalone version is a single executable that includes
echo everything needed to run (larger file size).
echo.
echo The Framework-Dependent version is smaller but requires
echo .NET 9.0 Runtime to be installed on the target machine.
echo.
@REM pause
