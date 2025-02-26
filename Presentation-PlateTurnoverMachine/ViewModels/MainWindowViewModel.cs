using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommonLibrary.Models;
using DeviceService;
using DeviceService.Camera;
using Presentation_CommonLibrary.Models;
using Presentation_CommonLibrary.Services;
using Presentation_PlateTurnoverMachine.Services;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;
using SixLabors.ImageSharp;

namespace Presentation_PlateTurnoverMachine.ViewModels;

public class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly ICustomDialogService _dialogService;
    private readonly ICameraService _cameraService;
    private readonly PhotoelectricManager _photoelectricManager;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly PackageTransferService _packageTransferService;
    private bool _disposed;
    private string _currentBarcode = string.Empty;
    private BitmapSource? _currentImage;
    private SystemStatus _systemStatus = new();

    public MainWindowViewModel(
        ICustomDialogService dialogService,
        ICameraService cameraService,
        PackageTransferService packageTransferService,
        PhotoelectricManager photoelectricManager)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _packageTransferService = packageTransferService;
        _photoelectricManager = photoelectricManager;

        // 初始化命令
        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);

        // 初始化系统状态更新定时器
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        // 初始化设备状态
        InitializeDeviceStatuses();

        // 初始化统计数据
        InitializeStatisticsItems();

        // 初始化包裹信息
        InitializePackageInfoItems();
        
        // 订阅相机连接状态事件
        _cameraService.ConnectionChanged += OnCameraConnectionChanged;
        
        // 订阅光电设备连接状态事件
        _photoelectricManager.DeviceConnectionStatusChanged += OnDeviceConnectionStatusChanged;
        
        // 订阅包裹流
        _subscriptions.Add(_packageTransferService.PackageStream
            .ObserveOn(Scheduler.CurrentThread)
            .Subscribe(OnPackageInfo));
        
        // 订阅图像流
        _subscriptions.Add(_cameraService.ImageStream
            .ObserveOn(Scheduler.CurrentThread)
            .Subscribe(imageData =>
            {
                try
                {
                    var image = imageData.image;
                    var barcodeLocations = imageData.barcodes;
                    
                    // 创建内存流并将其所有权转移给BitmapImage
                    var memoryStream = new MemoryStream();
                    // 由于现在是灰度图像，需要调整保存格式
                    image.SaveAsPng(memoryStream); // 使用PNG格式保持灰度图像质量
                    memoryStream.Position = 0;
                    
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.StreamSource = memoryStream;
                            bitmap.EndInit();
                            bitmap.Freeze(); // 使图像可以跨线程访问
                            
                            // 创建可绘制的图像
                            var drawingVisual = new DrawingVisual();
                            using (var drawingContext = drawingVisual.RenderOpen())
                            {
                                // 绘制原始图像
                                drawingContext.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
                                
                                // 绘制条码位置，使用更明显的颜色
                                foreach (var barcode in barcodeLocations)
                                {
                                    var pen = new Pen(Brushes.Red, 3); // 使用红色和更粗的线条，在灰度图上更容易看见
                                    pen.Freeze();
                                    
                                    // 创建条码框的路径
                                    var points = barcode.Points.Select(p => new System.Windows.Point(p.X, p.Y))
                                        .ToList();
                                    if (points.Count != 4) continue;
                                    {
                                        var geometry = new PathGeometry();
                                        var figure = new PathFigure(points[0], [
                                            new LineSegment(points[1], true),
                                            new LineSegment(points[2], true),
                                            new LineSegment(points[3], true),
                                            new LineSegment(points[0], true)
                                        ], true);
                                        geometry.Figures.Add(figure);
                                        geometry.Freeze();
                                        
                                        drawingContext.DrawGeometry(null, pen, geometry);
                                        
                                        // 只有在条码内容不为空时才绘制文本
                                        if (string.IsNullOrWhiteSpace(barcode.Code)) continue;
                                        // 绘制条码文本，使用更明显的颜色和大小
                                        var formattedText = new FormattedText(
                                            barcode.Code,
                                            CultureInfo.CurrentCulture,
                                            FlowDirection.LeftToRight,
                                            new Typeface("Arial"),
                                            16, // 增大字号
                                            Brushes.Red, // 使用红色
                                            VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip);
                                        formattedText.SetFontWeight(FontWeights.Bold);
                                        
                                        // 计算文本位置（在条码框的左上角）
                                        var textPoint = new System.Windows.Point(
                                            points.Min(p => p.X),
                                            points.Min(p => p.Y) - formattedText.Height);
                                        
                                        // 绘制文本背景
                                        var textBackground = new RectangleGeometry(new Rect(
                                            textPoint.X - 2,
                                            textPoint.Y - 2,
                                            formattedText.Width + 4,
                                            formattedText.Height + 4));
                                        drawingContext.DrawGeometry(Brushes.White, null, textBackground); // 使用白色背景
                                        
                                        // 绘制文本
                                        drawingContext.DrawText(formattedText, textPoint);
                                    }
                                }
                            }
                            
                            // 创建RenderTargetBitmap并渲染DrawingVisual
                            var renderBitmap = new RenderTargetBitmap(
                                bitmap.PixelWidth,
                                bitmap.PixelHeight,
                                96,
                                96,
                                PixelFormats.Pbgra32);
                            renderBitmap.Render(drawingVisual);
                            renderBitmap.Freeze();
                            
                            // 更新UI
                            CurrentImage = renderBitmap;
                        }
                        finally
                        {
                            memoryStream.Dispose();
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理图像数据时发生错误");
                }
            }));
    }

    #region Properties

    public DelegateCommand OpenSettingsCommand { get; }

    public string CurrentBarcode
    {
        get => _currentBarcode;
        private set => SetProperty(ref _currentBarcode, value);
    }

    public BitmapSource? CurrentImage
    {
        get => _currentImage;
        private set => SetProperty(ref _currentImage, value);
    }

    public SystemStatus SystemStatus
    {
        get => _systemStatus;
        private set => SetProperty(ref _systemStatus, value);
    }

    public ObservableCollection<PackageInfo> PackageHistory { get; } = [];
    public ObservableCollection<StatisticsItem> StatisticsItems { get; } = [];
    public ObservableCollection<DeviceStatus> DeviceStatuses { get; } = [];
    public ObservableCollection<PackageInfoItem> PackageInfoItems { get; } = [];

    #endregion

    #region Private Methods

    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsDialog");
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        SystemStatus = SystemStatus.GetCurrentStatus();
    }
    
    private void OnCameraConnectionChanged(string cameraId, bool isConnected)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var cameraStatus = DeviceStatuses.FirstOrDefault(s => s.Name == "相机");
            if (cameraStatus == null) return;
            
            cameraStatus.Status = isConnected ? "已连接" : "已断开";
            cameraStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
        });
    }
    
    private void OnDeviceConnectionStatusChanged(object? sender, (string Name, bool Connected) status)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            // 查找设备状态条目
            var deviceStatus = DeviceStatuses.FirstOrDefault(s => s.Name == status.Name);
            
            // 如果没有找到对应的设备条目，查找更通用的条目
            if (deviceStatus == null)
            {
                if (status.Name.Contains("触发光电"))
                {
                    deviceStatus = DeviceStatuses.FirstOrDefault(s => s.Name == "翻板机");
                }
                // 可添加其他设备的匹配逻辑
            }
            
            // 如果找到了设备条目，更新其状态
            if (deviceStatus != null)
            {
                deviceStatus.Status = status.Connected ? "已连接" : "已断开";
                deviceStatus.StatusColor = status.Connected ? "#4CAF50" : "#F44336";
                Log.Information("设备 {Name} 连接状态更新为 {Status}", status.Name, status.Connected ? "已连接" : "已断开");
            }
        });
    }
    
    private void OnPackageInfo(PackageInfo package)
    {
        try
        {
            Log.Information("收到包裹信息：{Barcode}", package.Barcode);
            
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    // 更新当前条码和图像
                    CurrentBarcode = package.Barcode;
                    // 更新实时包裹数据
                    UpdatePackageInfoItems(package);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "更新UI时发生错误");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹信息时发生错误：{Barcode}", package.Barcode);
            package.SetError($"处理失败：{ex.Message}");
        }
    }
    
    private void UpdatePackageInfoItems(PackageInfo package)
    {
        var weightItem = PackageInfoItems.FirstOrDefault(x => x.Label == "重量");
        if (weightItem != null)
        {
            weightItem.Value = package.Weight.ToString(CultureInfo.InvariantCulture);
            weightItem.Unit = "kg";
        }
        
        var sizeItem = PackageInfoItems.FirstOrDefault(x => x.Label == "尺寸");
        if (sizeItem != null)
        {
            sizeItem.Value = package.VolumeDisplay;
        }
        
        var chuteItem = PackageInfoItems.FirstOrDefault(x => x.Label == "格口");
        if (chuteItem != null)
        {
            chuteItem.Value = package.ChuteName.ToString();
            chuteItem.Description = string.IsNullOrEmpty(package.ChuteName.ToString()) ? "等待分配..." : "目标格口";
        }
        
        var timeItem = PackageInfoItems.FirstOrDefault(x => x.Label == "时间");
        if (timeItem != null)
        {
            timeItem.Value = package.CreateTime.ToString("HH:mm:ss");
            timeItem.Description = $"处理于 {package.CreateTime:yyyy-MM-dd}";
        }
        
        var statusItem = PackageInfoItems.FirstOrDefault(x => x.Label == "状态");
        if (statusItem == null) return;
        statusItem.Value = string.IsNullOrEmpty(package.ErrorMessage) ? "正常" : "异常";
        statusItem.Description = string.IsNullOrEmpty(package.ErrorMessage) ? "处理成功" : package.ErrorMessage;
    }

    private void InitializeDeviceStatuses()
    {
        try
        {
            Log.Debug("开始初始化设备状态列表");

            // 添加相机状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "相机",
                Status = "未连接",
                Icon = "Camera24",
                StatusColor = "#F44336" // 红色表示未连接
            });

            // 添加其他设备状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "翻板机",
                Status = "未连接",
                Icon = "DeviceLaptop24",
                StatusColor = "#F44336"
            });

            Log.Information("设备状态列表初始化完成，共 {Count} 个设备", DeviceStatuses.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化设备状态列表时发生错误");
        }
    }

    private void InitializeStatisticsItems()
    {
        StatisticsItems.Add(new StatisticsItem
        {
            Label = "总包裹数",
            Value = "0",
            Unit = "个",
            Description = "累计处理包裹总数",
            Icon = "BoxMultiple24"
        });

        StatisticsItems.Add(new StatisticsItem
        {
            Label = "成功数",
            Value = "0",
            Unit = "个",
            Description = "处理成功的包裹数量",
            Icon = "CheckmarkCircle24"
        });

        StatisticsItems.Add(new StatisticsItem
        {
            Label = "失败数",
            Value = "0",
            Unit = "个",
            Description = "处理失败的包裹数量",
            Icon = "ErrorCircle24"
        });

        StatisticsItems.Add(new StatisticsItem
        {
            Label = "处理速率",
            Value = "0",
            Unit = "个/小时",
            Description = "每小时处理包裹数量",
            Icon = "ArrowTrendingLines24"
        });
    }

    private void InitializePackageInfoItems()
    {
        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "重量",
            Value = "0.00",
            Unit = "kg",
            Description = "包裹重量",
            Icon = "Scale24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "尺寸",
            Value = "0 × 0 × 0",
            Unit = "mm",
            Description = "长 × 宽 × 高",
            Icon = "Ruler24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "格口",
            Value = "--",
            Description = "目标格口",
            Icon = "BoxMultiple24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "时间",
            Value = "--:--:--",
            Description = "处理时间",
            Icon = "Timer24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "状态",
            Value = "等待",
            Description = "处理状态",
            Icon = "AlertCircle24"
        });
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            try
            {
                // 取消订阅事件
                _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
                _photoelectricManager.DeviceConnectionStatusChanged -= OnDeviceConnectionStatusChanged;
                
                // 释放订阅
                foreach (var subscription in _subscriptions)
                {
                    subscription.Dispose();
                }
                
                _subscriptions.Clear();
                
                // 停止定时器
                _timer.Stop();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放资源时发生错误");
            }
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    #endregion
}