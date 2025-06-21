@echo off
chcp 65001 >nul
echo 🚀 TCP相机模拟器 - 发布脚本 v2.1
echo ================================
echo.

set "OUTPUT_DIR=publish"
set "APP_NAME=TcpCameraSimulator"

echo 请选择发布模式:
echo.
echo 1. 依赖运行时 (需要目标电脑安装.NET 8运行时，文件小约5MB)
echo 2. 自包含 (无需安装.NET运行时，文件大约67MB)
echo.
set /p mode="请选择 (1-2): "

if "%mode%"=="1" (
    set "PUBLISH_MODE=runtime-dependent"
    set "SELF_CONTAINED=false"
    set "EXPECTED_SIZE=5MB"
) else if "%mode%"=="2" (
    set "PUBLISH_MODE=self-contained"
    set "SELF_CONTAINED=true"
    set "EXPECTED_SIZE=67MB"
) else (
    echo 无效选择，默认使用依赖运行时模式
    set "PUBLISH_MODE=runtime-dependent"
    set "SELF_CONTAINED=false"
    set "EXPECTED_SIZE=5MB"
)

echo.
echo 正在清理旧的发布文件...
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"

echo.
echo 开始发布 %PUBLISH_MODE% 模式...
echo 目标平台: Windows x64
echo 输出目录: %OUTPUT_DIR%
echo 预期大小: %EXPECTED_SIZE%
echo.

if "%SELF_CONTAINED%"=="true" (
    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o "%OUTPUT_DIR%"
) else (
    dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "%OUTPUT_DIR%"
)

if errorlevel 1 (
    echo.
    echo ❌ 发布失败！请检查错误信息。
    pause
    exit /b 1
)

echo.
echo ✅ 发布成功！
echo.
echo 📁 发布文件位置: %OUTPUT_DIR%\
echo 📋 主要文件:
echo    - %APP_NAME%.exe           (主程序)
echo    - test-scripts.bat         (快速测试脚本)
echo    - README.md               (使用说明)
echo.

if exist "%OUTPUT_DIR%\%APP_NAME%.exe" (
    echo 📊 文件大小信息:
    for %%A in ("%OUTPUT_DIR%\%APP_NAME%.exe") do (
        set /a size_mb=%%~zA/1024/1024
        echo    主程序: %%~zA 字节 (约 !size_mb! MB^)
    )
    echo.
)

echo 💡 使用说明:
echo.
if "%SELF_CONTAINED%"=="true" (
    echo 📦 自包含模式:
    echo 1. 🗂️  复制整个 publish 文件夹到目标电脑
    echo 2. 📋  确保目标电脑是 Windows x64 系统
    echo 3. 🚀  直接运行 %APP_NAME%.exe (无需安装 .NET)
) else (
    echo 📦 依赖运行时模式:
    echo 1. 🗂️  复制整个 publish 文件夹到目标电脑
    echo 2. 📋  确保目标电脑已安装 .NET 8 运行时
    echo 3. 🚀  运行 %APP_NAME%.exe
    echo.
    echo ⚠️  重要: 目标电脑需要安装 .NET 8 运行时
    echo    下载地址: https://dotnet.microsoft.com/download/dotnet/8.0
)
echo 4. 📝  双击运行程序会自动进入协调模式(模式4)
echo 5. 📝  或使用 test-scripts.bat 进行选择
echo.
echo 🔧 命令行示例:
echo    直接协调模式: 双击 %APP_NAME%.exe
echo    标准测试:    %APP_NAME%.exe --clients 3 --rate 10
echo    协调模式:    %APP_NAME%.exe --coordinated --rate 15
echo    查看帮助:    %APP_NAME%.exe --help
echo.
echo 📖 详细说明请查看 README.md 文件
echo.

echo 是否立即测试发布的程序? (Y/N)
set /p test_choice="请选择: "

if /i "%test_choice%"=="Y" (
    echo.
    echo 🧪 启动测试...
    cd "%OUTPUT_DIR%"
    "%APP_NAME%.exe" --help
    echo.
    echo 🎯 测试完成！程序可以正常运行。
    cd ..
)

echo.
echo 🎉 发布流程完成！
echo.
if "%SELF_CONTAINED%"=="false" (
    echo ⚠️  注意: 此为依赖运行时模式，目标电脑需要安装 .NET 8 运行时
    echo.
)
pause 