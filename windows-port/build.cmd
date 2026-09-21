@echo off
rem ===========================================================================
rem  LiquidGlass 构建脚本（Windows）
rem
rem  用法：
rem    build.cmd            构建 Release 并输出到 dist\
rem    build.cmd debug      构建 Debug
rem    build.cmd test       构建并运行回归测试
rem    build.cmd clean      清理所有中间产物与输出
rem
rem  前置条件：只需 .NET 10 SDK。本仓库不依赖任何第三方 NuGet 包。
rem ===========================================================================
setlocal enabledelayedexpansion
cd /d "%~dp0"

rem ---------------------------------------------------------------------------
rem  补齐缺失的标准 Windows 環境变量。
rem
rem  某些受限 shell / 沙箱環境里，进程的環境块缺少一批标准变量
rem  （APPDATA、ALLUSERSPROFILE、PROGRAMDATA、PROGRAMFILES ...）。
rem  此时 NuGet 会崩在：
rem      System.IO.Path.Combine(String path1, String path2)
rem      at NuGet.Common.NuGetEnvironment.CalculateFolderPath(NuGetFolderPath)
rem      at NuGet.Configuration.XPlatMachineWideSetting..ctor()
rem  报 "Value cannot be null. (Parameter 'path1')"，
rem  而且**整个 dotnet 工具链都不可用**（连 `dotnet nuget list source` 都跑不了），
rem  不只是 restore/build —— 非常难定位，因为报错完全不提路径。
rem
rem  这里无条件补齐，正常 Windows 上重复赋值也无害。
rem ---------------------------------------------------------------------------
if "%APPDATA%"==""            set "APPDATA=%USERPROFILE%\AppData\Roaming"
if "%LOCALAPPDATA%"==""       set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"
if "%ALLUSERSPROFILE%"==""    set "ALLUSERSPROFILE=C:\ProgramData"
if "%PROGRAMDATA%"==""        set "PROGRAMDATA=C:\ProgramData"
if "%PROGRAMFILES%"==""       set "PROGRAMFILES=C:\Program Files"
if "%COMMONPROGRAMFILES%"=="" set "COMMONPROGRAMFILES=C:\Program Files\Common Files"
if "%PROCESSOR_ARCHITECTURE%"=="" set "PROCESSOR_ARCHITECTURE=AMD64"
if "%NUMBER_OF_PROCESSORS%"==""   set "NUMBER_OF_PROCESSORS=8"
if "%OS%"==""                 set "OS=Windows_NT"
if "%ComSpec%"==""            set "ComSpec=C:\Windows\system32\cmd.exe"
if "%COMPUTERNAME%"==""       set "COMPUTERNAME=%USERDOMAIN%"

set "CONFIG=Release"
if /i "%~1"=="debug" set "CONFIG=Debug"

if /i "%~1"=="clean" (
    echo [clean] 删除 bin / obj / dist ...
    for /d /r %%d in (bin,obj) do @if exist "%%d" rd /s /q "%%d"
    if exist dist rd /s /q dist
    echo [clean] 完成。
    exit /b 0
)

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [错误] 找不到 dotnet。请先安装 .NET 10 SDK：
    echo        https://dotnet.microsoft.com/download/dotnet/10.0
    exit /b 1
)

echo.
echo [1/4] 构建解决方案（%CONFIG%）...
dotnet build LiquidGlassWin.slnx -c %CONFIG% --nologo
if errorlevel 1 (
    echo [错误] 构建失败。
    exit /b 1
)

if /i "%~1"=="test" (
    echo.
    echo [2/4] 运行回归测试...
    dotnet run --project src\LiquidGlass.Tests\LiquidGlass.Tests.csproj -c %CONFIG% --no-build
    if errorlevel 1 (
        echo [错误] 测试未通过。
        exit /b 1
    )
) else (
    echo.
    echo [2/4] 运行回归测试...
    dotnet run --project src\LiquidGlass.Tests\LiquidGlass.Tests.csproj -c %CONFIG% --no-build
)

echo.
echo [3/4] 发布到 dist\ ...
set "APPOUT=src\LiquidGlass.App\bin\x64\%CONFIG%\net10.0-windows"
if not exist "%APPOUT%" set "APPOUT=src\LiquidGlass.App\bin\%CONFIG%\net10.0-windows"
if not exist "%APPOUT%" (
    echo [错误] 找不到构建产物：%APPOUT%
    exit /b 1
)

dotnet publish src\LiquidGlass.App\LiquidGlass.App.csproj -c %CONFIG% -r win-x64 --self-contained false -o dist\app --nologo -v q
dotnet publish src\LiquidGlass.Probe\LiquidGlass.Probe.csproj -c %CONFIG% -r win-x64 --self-contained false -o dist\probe --nologo -v q
dotnet publish src\LiquidGlass.Preview\LiquidGlass.Preview.csproj -c %CONFIG% -r win-x64 --self-contained false -o dist\preview --nologo -v q

echo.
echo [4/4] 复制配置模板与文档...
if not exist dist\config mkdir dist\config
copy /y config\liquidglass.json dist\config\ >nul
if exist README.md copy /y README.md dist\ >nul
if exist docs xcopy /e /i /q /y docs dist\docs >nul

echo.
echo ===========================================================================
echo  构建完成。
echo    主程序    dist\app\liquidglass.exe
echo    探针      dist\probe\liquidglass-probe.exe
echo    预览      dist\preview\liquidglass-preview.exe
echo    配置模板  dist\config\liquidglass.json
echo.
echo  首次使用建议顺序：
echo    1) dist\probe\liquidglass-probe.exe                  看看本机有哪些系统界面
echo    2) dist\probe\liquidglass-probe.exe --test-accent    确认任务栏能否透明化
echo    3) dist\preview\liquidglass-preview.exe              离线看玻璃长什么样
echo    4) dist\app\liquidglass.exe                          真正接管
echo ===========================================================================
endlocal
