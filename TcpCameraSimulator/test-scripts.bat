@echo off
chcp 65001 >nul
echo 🚀 TCP相机模拟器 - 快速测试脚本 v2.1
echo ======================================
echo.
echo 请选择测试场景:
echo.
echo 1. 标准性能测试 (3客户端, 15包/秒, 5分钟)
echo 2. 高并发压力测试 (10客户端, 50包/秒, 3分钟)  
echo 3. 背压修复验证 (5客户端, 突发100包, 2分钟)
echo 4. PLC+相机协调测试 (推荐 - 真实场景模拟)
echo 5. 高频协调测试 (20包/秒, 800-900ms延迟)
echo 6. 长期稳定性测试 (2客户端, 5包/秒, 60分钟)
echo 7. 自定义参数测试
echo 8. 显示帮助信息
echo 0. 退出
echo.

:start
set /p choice="请输入选择 (0-8): "

if "%choice%"=="1" goto standard_test
if "%choice%"=="2" goto high_concurrency_test  
if "%choice%"=="3" goto backpressure_test
if "%choice%"=="4" goto coordinated_test
if "%choice%"=="5" goto high_freq_coordinated_test
if "%choice%"=="6" goto stability_test
if "%choice%"=="7" goto custom_test
if "%choice%"=="8" goto show_help
if "%choice%"=="0" goto exit
goto invalid_choice

:standard_test
echo.
echo 🧪 启动标准性能测试...
echo 预期: 成功率 ^> 99%%, P95延迟 ^< 50ms, 吞吐量达成率 ^> 95%%
echo.
dotnet run -- --clients 3 --rate 15 --duration 300
goto end

:high_concurrency_test
echo.
echo 💪 启动高并发压力测试...
echo 关注: 连接稳定性, 延迟分布变化, 错误率增长
echo.
dotnet run -- --clients 10 --rate 50 --duration 180
goto end

:backpressure_test
echo.
echo 🎯 启动背压修复验证测试...
echo 验证: 无长时间阻塞, 突发负载下延迟稳定, 无136秒延迟
echo.
dotnet run -- --stress --clients 5 --burst 100 --duration 120
goto end

:coordinated_test
echo.
echo 🔧📸 启动PLC+相机协调测试（真实场景模拟）...
echo 场景: PLC发送信号 ^-^> 延迟800-900ms ^-^> 相机发送数据
echo 推荐: 用于验证背压修复效果，最接近生产环境
echo.
dotnet run -- --coordinated --rate 10 --duration 180
goto end

:high_freq_coordinated_test
echo.
echo 🔧📸 启动高频协调测试...
echo 场景: 高频PLC信号(20包/秒) + 相机延迟响应
echo 目标: 测试高负载下的协调性能
echo.
dotnet run -- --coordinated --rate 20 --camera-delay-min 800 --camera-delay-max 900 --duration 300
goto end

:stability_test
echo.
echo ⏰ 启动长期稳定性测试...
echo 提示: 此测试将运行60分钟，请确保有足够时间
echo.
set /p confirm="确认开始长期测试? (y/N): "
if /i "%confirm%"=="y" (
    dotnet run -- --clients 2 --rate 5 --duration 3600
) else (
    echo 测试已取消
)
goto end

:custom_test
echo.
echo 🛠️ 自定义参数测试
echo.
echo 测试模式选择:
echo   1. 标准模式 (独立相机客户端)
echo   2. 压测模式 (突发负载)
echo   3. 协调模式 (PLC+相机)
echo.
set /p mode="选择测试模式 (1-3): "

if "%mode%"=="3" goto custom_coordinated
if "%mode%"=="2" goto custom_stress

:custom_standard
echo.
echo 📸 标准模式配置
echo 当前默认值:
echo   相机服务器: 127.0.0.1:20011
echo   客户端数量: 3
echo   发送频率: 10 包/秒
echo   测试时长: 60 秒
echo.
set /p host="相机服务器地址 (回车使用默认): "
set /p port="相机服务器端口 (回车使用默认): "
set /p clients="客户端数量 (回车使用默认): "
set /p rate="发送频率 (回车使用默认): "
set /p duration="测试时长/秒 (回车使用默认): "

set cmd=dotnet run --
if not "%host%"=="" set cmd=%cmd% --host %host%
if not "%port%"=="" set cmd=%cmd% --port %port%
if not "%clients%"=="" set cmd=%cmd% --clients %clients%
if not "%rate%"=="" set cmd=%cmd% --rate %rate%
if not "%duration%"=="" set cmd=%cmd% --duration %duration%
goto execute_custom

:custom_stress
echo.
echo 💪 压测模式配置
echo 当前默认值:
echo   相机服务器: 127.0.0.1:20011
echo   客户端数量: 5
echo   突发批量: 20 包/批
echo   测试时长: 120 秒
echo.
set /p host="相机服务器地址 (回车使用默认): "
set /p port="相机服务器端口 (回车使用默认): "
set /p clients="客户端数量 (回车使用默认): "
set /p burst="突发批量 (回车使用默认): "
set /p duration="测试时长/秒 (回车使用默认): "

set cmd=dotnet run -- --stress
if not "%host%"=="" set cmd=%cmd% --host %host%
if not "%port%"=="" set cmd=%cmd% --port %port%
if not "%clients%"=="" set cmd=%cmd% --clients %clients%
if not "%burst%"=="" set cmd=%cmd% --burst %burst%
if not "%duration%"=="" set cmd=%cmd% --duration %duration%
goto execute_custom

:custom_coordinated
echo.
echo 🔧📸 协调模式配置
echo 当前默认值:
echo   PLC服务器: 127.0.0.1:20010
echo   相机服务器: 127.0.0.1:20011
echo   PLC频率: 10 包/秒
echo   相机延迟: 800-900ms
echo   测试时长: 180 秒
echo.
set /p plc_host="PLC服务器地址 (回车使用默认): "
set /p plc_port="PLC服务器端口 (回车使用默认): "
set /p camera_host="相机服务器地址 (回车使用默认): "
set /p camera_port="相机服务器端口 (回车使用默认): "
set /p rate="PLC信号频率 (回车使用默认): "
set /p delay_min="相机延迟最小值/ms (回车使用默认): "
set /p delay_max="相机延迟最大值/ms (回车使用默认): "
set /p duration="测试时长/秒 (回车使用默认): "

set cmd=dotnet run -- --coordinated
if not "%plc_host%"=="" set cmd=%cmd% --plc-host %plc_host%
if not "%plc_port%"=="" set cmd=%cmd% --plc-port %plc_port%
if not "%camera_host%"=="" set cmd=%cmd% --host %camera_host%
if not "%camera_port%"=="" set cmd=%cmd% --port %camera_port%
if not "%rate%"=="" set cmd=%cmd% --rate %rate%
if not "%delay_min%"=="" set cmd=%cmd% --camera-delay-min %delay_min%
if not "%delay_max%"=="" set cmd=%cmd% --camera-delay-max %delay_max%
if not "%duration%"=="" set cmd=%cmd% --duration %duration%
goto execute_custom

:execute_custom
echo.
echo 执行命令: %cmd%
echo.
%cmd%
goto end

:show_help
echo.
dotnet run -- --help
echo.
echo ======================================
echo 💡 测试场景建议:
echo.
echo 🎯 背压修复验证:
echo    - 使用场景4 (协调模式) 最为推荐
echo    - 真实模拟PLC信号触发相机的生产环境
echo    - 能准确复现您遇到的136秒延迟问题
echo.
echo 📊 性能基准测试:
echo    - 使用场景1 (标准模式) 建立基准
echo    - 使用场景2 (高并发) 测试极限性能
echo.
echo 🔧 协调模式优势:
echo    - 模拟真实的PLC和相机时序
echo    - 验证修复后无长时间阻塞
echo    - 监控信号匹配率和延迟分布
echo ======================================
goto end

:invalid_choice
echo.
echo ❌ 无效选择，请输入 0-8 之间的数字
echo.
pause
goto start

:end
echo.
echo 测试完成！
echo.
echo 📋 测试结果分析提示:
echo   - 关注P95/P99延迟是否稳定在200ms以下
echo   - 检查是否出现"相机数据流处理延迟异常"警告
echo   - 协调模式下观察信号匹配率应接近100%%
echo   - 无136秒等极端延迟表示修复成功
echo.
pause

:exit
echo.
echo 👋 再见！