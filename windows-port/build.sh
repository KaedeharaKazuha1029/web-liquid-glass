#!/usr/bin/env bash
# ===========================================================================
#  LiquidGlass 构建脚本（bash / Git Bash / WSL 调用 Windows 上的 dotnet）
#
#  用法：
#    ./build.sh             构建 Release 并输出到 dist/
#    ./build.sh debug       构建 Debug
#    ./build.sh clean       清理
# ===========================================================================
set -euo pipefail
cd "$(dirname "$0")"

CONFIG="Release"
case "${1:-}" in
  debug|Debug) CONFIG="Debug" ;;
  clean)
    echo "[clean] 删除 bin / obj / dist ..."
    find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + 2>/dev/null || true
    rm -rf dist
    echo "[clean] 完成。"
    exit 0
    ;;
esac

if ! command -v dotnet >/dev/null 2>&1; then
  echo "[错误] 找不到 dotnet。请先安装 .NET 10 SDK。" >&2
  exit 1
fi

echo
echo "[1/4] 构建解决方案（$CONFIG）..."
dotnet build LiquidGlassWin.slnx -c "$CONFIG" --nologo

echo
echo "[2/4] 运行回归测试..."
dotnet run --project src/LiquidGlass.Tests/LiquidGlass.Tests.csproj -c "$CONFIG" --no-build

echo
echo "[3/4] 发布到 dist/ ..."
dotnet publish src/LiquidGlass.App/LiquidGlass.App.csproj \
  -c "$CONFIG" -r win-x64 --self-contained false -o dist/app --nologo -v q
dotnet publish src/LiquidGlass.Probe/LiquidGlass.Probe.csproj \
  -c "$CONFIG" -r win-x64 --self-contained false -o dist/probe --nologo -v q
dotnet publish src/LiquidGlass.Preview/LiquidGlass.Preview.csproj \
  -c "$CONFIG" -r win-x64 --self-contained false -o dist/preview --nologo -v q

echo
echo "[4/4] 复制配置模板与文档..."
mkdir -p dist/config
cp config/liquidglass.json dist/config/
[ -f README.md ] && cp README.md dist/
[ -d docs ] && cp -r docs dist/

cat <<'EOF'

===========================================================================
 构建完成。
   主程序    dist/app/liquidglass.exe
   探针      dist/probe/liquidglass-probe.exe
   预览      dist/preview/liquidglass-preview.exe
   配置模板  dist/config/liquidglass.json

 首次使用建议顺序：
   1) dist/probe/liquidglass-probe.exe                看看本机有哪些系统界面
   2) dist/probe/liquidglass-probe.exe --test-accent  确认任务栏能否透明化
   3) dist/preview/liquidglass-preview.exe            离线看玻璃长什么样
   4) dist/app/liquidglass.exe                        真正接管
===========================================================================
EOF
