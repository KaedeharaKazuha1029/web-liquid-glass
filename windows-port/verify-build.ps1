# ===========================================================================
#  验证 LiquidGlass 产物里确实含新版代码的符号
#
#  为什么需要它：v3.2.0 和 v3.3.0 两次交付都失败在同一个地方 ——
#  打包时 dist\ 被运行中的进程锁定，改了输出目录，但**没核对最终二进制**，
#  结果用户装的是旧版，症状一个没少，白跑两轮。
#
#  用法： powershell -ExecutionPolicy Bypass -File verify-build.ps1
# ===========================================================================

$ErrorActionPreference = "Stop"

$dlls = @(
    "dist\app\LiquidGlass.Win32.dll",
    "dist\app\LiquidGlass.Core.dll"
)

# v3.4.0 新增的符号。少一个就说明产物是旧版。
$expected = @{
    "LiquidGlass.Win32.dll" = @(
        "AlignToPanel",                    # 任务栏：上屏帧尺寸对齐（乱横线）
        "InvokeTrayIcon",                  # 任务栏：托盘点击分流
        "skipPresentUntilTicks",           # 任务栏：布局变化后跳过上屏
        "PresentIntervalWhileAccumulating",# 开始菜单：累积期间上屏限频
        "contentLayer",                    # 开始菜单：内容层缓存
        "iconsHidden"                      # 开始菜单：桌面图标生命周期
    )
    "LiquidGlass.Core.dll" = @(
        "UpscaleBilinear"                  # 定点放大（合成提速）
    )
}

Write-Host ""
Write-Host "============================================================"
Write-Host " 验证产物"
Write-Host "============================================================"
Write-Host ""

$fail = 0

foreach ($rel in $dlls) {
    if (-not (Test-Path $rel)) {
        Write-Host "  [缺失] $rel 不存在" -ForegroundColor Red
        $fail = 1
        continue
    }

    $item = Get-Item $rel
    $name = $item.Name
    Write-Host "  文件: $rel"
    Write-Host "        大小 $($item.Length) 字节   修改时间 $($item.LastWriteTime)"
    Write-Host ""

    # 读成字节再转 ASCII —— 直接 Get-Content 会因编码/BOM 出问题。
    # ⚠️ .NET 的程序集元数据字符串是 **UTF-8**（不是 UTF-16），
    #    所以用 ASCII/UTF8 都能搜到；用 Unicode 会一无所获。
    $bytes = [System.IO.File]::ReadAllBytes($item.FullName)
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)

    $want = $expected[$name]
    if (-not $want) { continue }

    foreach ($sym in $want) {
        if ($text.Contains($sym)) {
            Write-Host "    [OK]   $sym" -ForegroundColor Green
        } else {
            Write-Host "    [缺失] $sym" -ForegroundColor Red
            $fail = 1
        }
    }
    Write-Host ""
}

if ($fail -eq 1) {
    Write-Host "============================================================"
    Write-Host " 验证失败：产物里缺少新代码。" -ForegroundColor Red
    Write-Host " 装上去不会有任何变化 —— 请把完整输出发给我。" -ForegroundColor Red
    Write-Host "============================================================"
    exit 1
}

Write-Host "============================================================"
Write-Host " 验证通过：产物确实含新代码。" -ForegroundColor Green
Write-Host "============================================================"
exit 0
