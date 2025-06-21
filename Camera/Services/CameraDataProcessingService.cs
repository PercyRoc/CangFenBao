using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Camera.Interface;
using Camera.Models.Settings;
using Common.Models.Package;
using Common.Services.Settings;
using Serilog;

namespace Camera.Services
{
    /// <summary>
    /// 数据中转服务，用于处理条码过滤、重复过滤和图像保存等。
    /// 订阅来自相机接口的数据流，经过处理之后向下传递。
    /// </summary>
    public sealed class CameraDataProcessingService:IDisposable
    {
        private readonly ICameraService _actualCameraService;
        private readonly ISettingsService _settingsService;

        private readonly Subject<PackageInfo> _processedPackageSubject = new();
        private readonly Subject<(BitmapSource Image, string CameraId)> _processedImageWithIdSubject = new();

        private IDisposable? _packageSubscription;
        private IDisposable? _imageSubscription;

        // 【关键改进】为包裹匹配和处理创建一个专用的线程和调度器
        private readonly Thread _packageMatchingThread;
        private readonly EventLoopScheduler _packageMatchingScheduler;
        private readonly CancellationTokenSource _cts = new();

        // State for the new N-2 barcode duplication filter
        private string? _nMinus1BarcodeForFilter;
        private string? _nMinus2BarcodeForFilter;
        private readonly object _nBackFilterLock = new();

        public bool IsConnected => _actualCameraService.IsConnected;
        public IObservable<PackageInfo> PackageStream => _processedPackageSubject.AsObservable();
        public IObservable<(BitmapSource Image, string CameraId)> ImageStreamWithId => _processedImageWithIdSubject.AsObservable();

        public event Action<string?, bool>? ConnectionChanged
        {
            add => _actualCameraService.ConnectionChanged += value;
            remove => _actualCameraService.ConnectionChanged -= value;
        }

        public CameraDataProcessingService(ICameraService actualCameraService, ISettingsService settingsService)
        {
            _actualCameraService = actualCameraService ?? throw new ArgumentNullException(nameof(actualCameraService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

            // 【关键改进】初始化专用线程和调度器
            _packageMatchingThread = new Thread(ThreadStart)
            {
                Name = "PackageMatchingThread",
                IsBackground = true,
                Priority = ThreadPriority.Normal // 正常优先级，低于TCP接收线程
            };
            
            _packageMatchingScheduler = new EventLoopScheduler(ts => _packageMatchingThread);
            
            _packageMatchingThread.Start();
            Log.Information("✅ [专用匹配线程] 线程 'PackageMatchingThread' 已启动。");
        }
        
        private void ThreadStart()
        {
            Log.Debug("专用匹配线程循环开始。");
            // EventLoopScheduler 会处理循环和等待，我们只需要保持线程存活
            _cts.Token.WaitHandle.WaitOne();
            Log.Debug("专用匹配线程循环结束。");
        }

        public bool Start()
        {
            Log.Information("[相机数据处理服务] 正在启动订阅...");
  
            _packageSubscription?.Dispose();
            // 【关键改进】将包裹处理调度到专用的匹配线程上，而不是线程池
            _packageSubscription = _actualCameraService.PackageStream
                .ObserveOn(_packageMatchingScheduler) 
                .Subscribe(HandleRawPackage, OnStreamError, OnPackageStreamCompleted);

            _imageSubscription?.Dispose();
            _imageSubscription = _actualCameraService.ImageStreamWithId
                .ObserveOn(TaskPoolScheduler.Default)
                .Subscribe(
                    _processedImageWithIdSubject.OnNext, 
                    OnStreamError,
                    _processedImageWithIdSubject.OnCompleted
                );
            
            Log.Information("[相机数据处理服务] 已订阅实际相机流。");
            return true;
        }

        private void HandleRawPackage(PackageInfo package)
        {
            var settings = _settingsService.LoadSettings<CameraOverallSettings>();

            if (settings.IsBarcodeFilterEnabled && !IsBarcodeValid(package, settings.BarcodeFilter))
            {
                Log.Information("[相机数据处理服务] 条码 '{Barcode}' 被规则过滤.", package.Barcode);
                package.SetStatus("Filtered");
                package.Dispose(); 
                return;
            }

            if (settings.IsBarcodeDuplicationEnabled && IsBarcodeDuplicateAndFilter(package))
            {
                // 日志已在 IsBarcodeDuplicateAndFilter 方法内部记录，如果它被过滤。
                // 此处不需要额外日志，除非要标记为重复状态。
                package.SetStatus("Duplicate"); 
                package.Dispose();
                return;
            }
            
            if (settings.IsImageSaveEnabled && package.Image != null && !string.IsNullOrEmpty(package.Barcode))
            {
                if (package.AdditionalData.TryGetValue("CameraId", out var camIdObj) && camIdObj is string camIdValue)
                {
                    if (!string.IsNullOrWhiteSpace(camIdValue))
                    {
                    }
                }
                // cameraIdForFileName 目前未使用在新的文件名格式中，但保留以备将来可能的需求。
                SaveImageIfEnabled(package.Image, package.Barcode, settings.ImageSave);
            }
            
            _processedPackageSubject.OnNext(package);
        }

        private static bool IsBarcodeValid(PackageInfo package, BarcodeFilterSettings filterSettings)
        {
            if (string.IsNullOrEmpty(package.Barcode)) return true; 

            foreach (var group in filterSettings.RuleGroups.Where(g => g.IsGroupEnabled))
            {
                if (group.LengthRule.IsEnabled)
                {
                    var barcodeLength = package.Barcode.Length;
                    if ((group.LengthRule.MinLength.HasValue && barcodeLength < group.LengthRule.MinLength.Value) ||
                        (group.LengthRule.MaxLength.HasValue && barcodeLength > group.LengthRule.MaxLength.Value))
                    {
                        Log.Verbose("条码 '{Barcode}' 因长度规则在组 '{GroupName}' 中验证失败.", package.Barcode, group.GroupName);
                        return false; 
                    }
                }

                if (group.StartsWithRule.IsEnabled && !string.IsNullOrEmpty(group.StartsWithRule.Pattern) &&
                    !package.Barcode.StartsWith(group.StartsWithRule.Pattern, StringComparison.OrdinalIgnoreCase))
                {
                     Log.Verbose("条码 '{Barcode}' 因 StartsWith 规则 ('{Pattern}') 在组 '{GroupName}' 中验证失败.", package.Barcode, group.StartsWithRule.Pattern, group.GroupName);
                    return false;
                }

                if (group.EndsWithRule.IsEnabled && !string.IsNullOrEmpty(group.EndsWithRule.Pattern) &&
                    !package.Barcode.EndsWith(group.EndsWithRule.Pattern, StringComparison.OrdinalIgnoreCase))
                {
                     Log.Verbose("条码 '{Barcode}' 因 EndsWith 规则 ('{Pattern}') 在组 '{GroupName}' 中验证失败.", package.Barcode, group.EndsWithRule.Pattern, group.GroupName);
                    return false;
                }

                if (group.ContainsRule.IsEnabled && !string.IsNullOrEmpty(group.ContainsRule.Pattern) &&
                    !package.Barcode.Contains(group.ContainsRule.Pattern, StringComparison.OrdinalIgnoreCase))
                {
                     Log.Verbose("条码 '{Barcode}' 因 Contains 规则 ('{Pattern}') 在组 '{GroupName}' 中验证失败.", package.Barcode, group.ContainsRule.Pattern, group.GroupName);
                    return false;
                }
                
                if (group.NotContainsRule.IsEnabled && !string.IsNullOrEmpty(group.NotContainsRule.Pattern) &&
                    package.Barcode.Contains(group.NotContainsRule.Pattern, StringComparison.OrdinalIgnoreCase))
                {
                     Log.Verbose("条码 '{Barcode}' 因 NotContains 规则 ('{Pattern}') 在组 '{GroupName}' 中验证失败.", package.Barcode, group.NotContainsRule.Pattern, group.GroupName);
                    return false;
                }

                if (group.CharTypeRule.IsEnabled)
                {
                    var charRulePassed = group.CharTypeRule.CharacterType switch
                    {
                        BarcodeCharacterType.AllDigits => package.Barcode.All(char.IsDigit),
                        BarcodeCharacterType.AllLetters => package.Barcode.All(char.IsLetter),
                        BarcodeCharacterType.DigitsAndLetters => package.Barcode.All(char.IsLetterOrDigit),
                        _ => true 
                    };
                    if (!charRulePassed)
                    {
                        Log.Verbose("条码 '{Barcode}' 因字符类型规则 ({CharType}) 在组 '{GroupName}' 中验证失败.", package.Barcode, group.CharTypeRule.CharacterType, group.GroupName);
                        return false;
                    }
                }

                if (!group.CustomRegexRule.IsEnabled ||
                    string.IsNullOrEmpty(group.CustomRegexRule.RegexPattern)) continue;
                try
                {
                    if (Regex.IsMatch(package.Barcode, group.CustomRegexRule.RegexPattern)) continue;
                    Log.Verbose("条码 '{Barcode}' 因 Regex 规则 ('{Pattern}') 在组 '{GroupName}' 中验证失败.", package.Barcode, group.CustomRegexRule.RegexPattern, group.GroupName);
                    return false;
                }
                catch (ArgumentException ex) 
                {
                    Log.Warning(ex, "无效的 Regex 表达式 '{Pattern}' 在条码过滤组 '{GroupName}' 中. 此规则已跳过.", group.CustomRegexRule.RegexPattern, group.GroupName);
                }
            }
            return true; 
        }

        /// <summary>
        /// 检查条码是否重复，并根据新的 N-2 规则决定是否过滤。如果被过滤，则返回 true。
        /// </summary>
        private bool IsBarcodeDuplicateAndFilter(PackageInfo package)
        {
            if (string.IsNullOrEmpty(package.Barcode)) return false; // 空条码不参与重复过滤

            var currentBarcode = package.Barcode;

            lock (_nBackFilterLock)
            {
                // Filter condition: current barcode is the same as the one before the previous one (_nMinus2BarcodeForFilter).
                if (_nMinus2BarcodeForFilter != null && currentBarcode == _nMinus2BarcodeForFilter)
                {
                    Log.Information("[相机数据处理服务] 条码 '{Barcode}' 与上上一次条码 '{NMinus2Barcode}' 相同 (N-2 规则)，判定为重复并过滤.",
                                    currentBarcode, _nMinus2BarcodeForFilter);
                    return true; // Filter
                }
                else
                {
                    // Not filtered by N-2 rule. Update history for the next check.
                    // This effectively shifts the history: current becomes N-1, old N-1 becomes N-2.
                    _nMinus2BarcodeForFilter = _nMinus1BarcodeForFilter;
                    _nMinus1BarcodeForFilter = currentBarcode;
                    Log.Verbose("[相机数据处理服务] 条码 '{Barcode}' 通过 N-2 重复检查。N-1 更新为 '{N1}', N-2 更新为 '{N2}'.",
                                currentBarcode, _nMinus1BarcodeForFilter, _nMinus2BarcodeForFilter ?? "null");
                    return false; // Do not filter
                }
            }
        }

        private static void SaveImageIfEnabled(BitmapSource image, string barcode, ImageSaveSettings saveSettings)
        {
            Task.Run(() =>
            {
                try
                {
                    var dateFolder = DateTime.Now.ToString("yyyyMMdd");
                    var dailyFolderPath = Path.Combine(saveSettings.SaveFolderPath, dateFolder);

                    if (!Directory.Exists(dailyFolderPath))
                    {
                        Directory.CreateDirectory(dailyFolderPath);
                    }

                    var timeStampForFile = DateTime.Now.ToString("HHmmss_fff");
                    string sanitizedBarcode = new([.. barcode.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch))]);
                    if (string.IsNullOrWhiteSpace(sanitizedBarcode)) sanitizedBarcode = "NoBarcode";
                    
                    var fileName = $"{sanitizedBarcode}_{timeStampForFile}.png";
                    var filePath = Path.Combine(dailyFolderPath, fileName);

                    BitmapEncoder encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));

                    using (var fileStream = new FileStream(filePath, FileMode.Create))
                    {
                        encoder.Save(fileStream);
                    }
                    Log.Information("[相机数据处理服务] 图像已保存 (条码: '{Barcode}') 至 '{FilePath}'.", barcode, filePath);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[相机数据处理服务] 保存图像失败 (条码: '{Barcode}').", barcode);
                }
            });
        }

        public bool Stop()
        {
            Log.Information("[相机数据处理服务] 正在停止订阅...");
            _packageSubscription?.Dispose();
            _packageSubscription = null;
            _imageSubscription?.Dispose();
            _imageSubscription = null;
            
            Log.Information("[相机数据处理服务] 订阅已停止。");
            return true;
        }
        
        private void OnStreamError(Exception ex)
        {
            Log.Error(ex, "[相机数据处理服务] 底层相机流发生错误。");
            _processedPackageSubject.OnError(ex);
            _processedImageWithIdSubject.OnError(ex);
        }

        private void OnPackageStreamCompleted()
        {
            Log.Information("[相机数据处理服务] 底层包裹流已完成。");
            _processedPackageSubject.OnCompleted();
        }
        
        private bool _disposedValue;
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposedValue) return;
            if (disposing)
            {
                Log.Debug("[相机数据处理服务] 正在释放托管资源。");
                
                // 【关键改进】优雅地停止专用线程
                _cts.Cancel();
                _packageMatchingScheduler.Dispose();
                
                Stop(); 
                    
                _processedPackageSubject.OnCompleted(); 
                _processedPackageSubject.Dispose();
                _processedImageWithIdSubject.OnCompleted();
                _processedImageWithIdSubject.Dispose();
                
                _cts.Dispose();
            }
            _disposedValue = true;
            Log.Debug("[相机数据处理服务] 已释放。");
        }

        ~CameraDataProcessingService()
        {
            Dispose(disposing: false);
        }
    }
} 