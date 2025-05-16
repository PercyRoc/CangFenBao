using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Camera.Interface;
using Camera.Models;
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
    public sealed class CameraDataProcessingService(ICameraService actualCameraService, ISettingsService settingsService):IDisposable
    {
        private readonly ICameraService _actualCameraService = actualCameraService ?? throw new ArgumentNullException(nameof(actualCameraService));
        private readonly ISettingsService _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

        private readonly Subject<PackageInfo> _processedPackageSubject = new();
        private readonly Subject<(BitmapSource Image, string CameraId)> _processedImageWithIdSubject = new();

        private IDisposable? _packageSubscription;
        private IDisposable? _imageSubscription;

        // 用于条码重复过滤：存储 (条码, 首次出现时间, 最后一次出现时间, 当前窗口内计数)
        private readonly List<(string Barcode, DateTime FirstSeen, DateTime LastSeen, int Count)> _recentBarcodeInfo = new();
        private readonly object _barcodeHistoryLock = new();

        public bool IsConnected => _actualCameraService.IsConnected;
        public IObservable<PackageInfo> PackageStream => _processedPackageSubject.AsObservable();
        public IObservable<(BitmapSource Image, string CameraId)> ImageStreamWithId => _processedImageWithIdSubject.AsObservable();

        public event Action<string?, bool>? ConnectionChanged
        {
            add => _actualCameraService.ConnectionChanged += value;
            remove => _actualCameraService.ConnectionChanged -= value;
        }

        public bool Start()
        {
            Log.Information("[相机数据处理服务] 正在启动订阅...");
  
            _packageSubscription?.Dispose();
            _packageSubscription = _actualCameraService.PackageStream
                .ObserveOn(TaskPoolScheduler.Default) 
                .Subscribe(HandleRawPackage, OnStreamError, OnPackageStreamCompleted);

            _imageSubscription?.Dispose();
            _imageSubscription = _actualCameraService.ImageStreamWithId
                .ObserveOn(TaskPoolScheduler.Default)
                .Subscribe(
                    rawImageTuple => _processedImageWithIdSubject.OnNext(rawImageTuple), 
                    OnStreamError, 
                    () => _processedImageWithIdSubject.OnCompleted()
                );
            
            Log.Information("[相机数据处理服务] 已订阅实际相机流。");
            return true;
        }

        private void HandleRawPackage(PackageInfo package)
        {
            var settings = _settingsService.LoadSettings<CameraOverallSettings>() ?? new CameraOverallSettings();

            if (settings.IsBarcodeFilterEnabled && !IsBarcodeValid(package, settings.BarcodeFilter))
            {
                Log.Information("[相机数据处理服务] 条码 '{Barcode}' 被规则过滤.", package.Barcode);
                package.SetStatus(PackageStatus.Filtered);
                package.Dispose(); 
                return;
            }

            if (settings.IsBarcodeDuplicationEnabled && IsBarcodeDuplicateAndFilter(package, settings.BarcodeDuplication))
            {
                // 日志已在 IsBarcodeDuplicateAndFilter 方法内部记录，如果它被过滤。
                // 此处不需要额外日志，除非要标记为重复状态。
                package.SetStatus(PackageStatus.Duplicate); 
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
                    int barcodeLength = package.Barcode.Length;
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
                    bool charRulePassed = group.CharTypeRule.CharacterType switch
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

                if (group.CustomRegexRule.IsEnabled && !string.IsNullOrEmpty(group.CustomRegexRule.RegexPattern))
                {
                    try
                    {
                        if (!Regex.IsMatch(package.Barcode, group.CustomRegexRule.RegexPattern))
                        {
                            Log.Verbose("条码 '{Barcode}' 因 Regex 规则 ('{Pattern}') 在组 '{GroupName}' 中验证失败.", package.Barcode, group.CustomRegexRule.RegexPattern, group.GroupName);
                            return false;
                        }
                    }
                    catch (ArgumentException ex) 
                    {
                        Log.Warning(ex, "无效的 Regex 表达式 '{Pattern}' 在条码过滤组 '{GroupName}' 中. 此规则已跳过.", group.CustomRegexRule.RegexPattern, group.GroupName);
                    }
                }
            }
            return true; 
        }

        /// <summary>
        /// 检查条码是否重复，并根据规则决定是否过滤。如果被过滤，则返回 true。
        /// </summary>
        private bool IsBarcodeDuplicateAndFilter(PackageInfo package, BarcodeDuplicationSettings duplicationSettings)
        {
            if (string.IsNullOrEmpty(package.Barcode)) return false; // 空条码不参与重复过滤

            DateTime now = DateTime.UtcNow;
            string currentBarcode = package.Barcode;

            lock (_barcodeHistoryLock)
            {
                // 清理非常陈旧的条目 (例如，超过重复时间窗口数倍的)
                _recentBarcodeInfo.RemoveAll(entry => (now - entry.LastSeen).TotalMilliseconds > duplicationSettings.DuplicationTimeMs * 5);

                int existingIndex = _recentBarcodeInfo.FindIndex(entry => entry.Barcode == currentBarcode);

                if (existingIndex != -1)
                {
                    // 条码已存在
                    var existingEntry = _recentBarcodeInfo[existingIndex];

                    if ((now - existingEntry.FirstSeen).TotalMilliseconds > duplicationSettings.DuplicationTimeMs)
                    {
                        // 超出时间窗口，重置计数和时间，不视作重复（即，继续处理）
                        _recentBarcodeInfo[existingIndex] = (currentBarcode, now, now, 1);
                        Log.Verbose("[相机数据处理服务] 条码 '{Barcode}' 超出重复时间窗口 ({TimeMs}ms)，重置计数并处理.", currentBarcode, duplicationSettings.DuplicationTimeMs);
                        return false; // 不过滤
                    }
                    else
                    {
                        // 在时间窗口内
                        if (existingEntry.Count < duplicationSettings.FilterCount)
                        {
                            // 未达到过滤次数上限，增加计数，标记为重复并过滤
                            _recentBarcodeInfo[existingIndex] = (existingEntry.Barcode, existingEntry.FirstSeen, now, existingEntry.Count + 1);
                            Log.Information("[相机数据处理服务] 条码 '{Barcode}' 在 {TimeMs}ms 内出现 {Count}/{MaxCount} 次，判定为重复并过滤.", 
                                currentBarcode, duplicationSettings.DuplicationTimeMs, existingEntry.Count + 1, duplicationSettings.FilterCount);
                            return true; // 过滤
                        }
                        else
                        {
                            // 已达到或超过过滤次数上限，重置计数和时间，视为新序列的开始（即，继续处理）
                            _recentBarcodeInfo[existingIndex] = (currentBarcode, now, now, 1);
                            Log.Information("[相机数据处理服务] 条码 '{Barcode}' 在 {TimeMs}ms 内达到重复次数上限 ({MaxCount})，重置计数并处理.", 
                                currentBarcode, duplicationSettings.DuplicationTimeMs, duplicationSettings.FilterCount);
                            return false; // 不过滤
                        }
                    }
                }
                else
                {
                    // 新条码，添加并处理
                    _recentBarcodeInfo.Add((currentBarcode, now, now, 1));
                    // 限制列表大小，防止无限增长。FilterCount * N (e.g., 5) seems reasonable.
                    while (_recentBarcodeInfo.Count > duplicationSettings.FilterCount * 5 && _recentBarcodeInfo.Count > 0) 
                    {
                        _recentBarcodeInfo.RemoveAt(0); // Remove the oldest based on LastSeen or insertion order
                    }
                    Log.Verbose("[相机数据处理服务] 新条码 '{Barcode}'，添加到重复检测列表并处理.", currentBarcode);
                    return false; // 不过滤
                }
            }
        }

        private static void SaveImageIfEnabled(BitmapSource image, string barcode, ImageSaveSettings saveSettings)
        {
            Task.Run(() =>
            {
                try
                {
                    string dateFolder = DateTime.Now.ToString("yyyyMMdd");
                    string dailyFolderPath = Path.Combine(saveSettings.SaveFolderPath, dateFolder);

                    if (!Directory.Exists(dailyFolderPath))
                    {
                        Directory.CreateDirectory(dailyFolderPath);
                    }

                    string timeStampForFile = DateTime.Now.ToString("HHmmss_fff");
                    string sanitizedBarcode = new string(barcode.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)).ToArray());
                    if (string.IsNullOrWhiteSpace(sanitizedBarcode)) sanitizedBarcode = "NoBarcode";
                    
                    string fileName = $"{sanitizedBarcode}_{timeStampForFile}.png";
                    string filePath = Path.Combine(dailyFolderPath, fileName);

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

        public IEnumerable<CameraInfo> GetAvailableCameras()
        {
            return _actualCameraService.GetAvailableCameras();
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
            if (!_disposedValue)
            {
                if (disposing)
                {
                    Log.Debug("[相机数据处理服务] 正在释放托管资源。");
                    Stop(); 
                    
                    _processedPackageSubject.OnCompleted(); 
                    _processedPackageSubject.Dispose();
                    _processedImageWithIdSubject.OnCompleted();
                    _processedImageWithIdSubject.Dispose();

                }
                _disposedValue = true;
                Log.Debug("[相机数据处理服务] 已释放。");
            }
        }

        ~CameraDataProcessingService()
        {
            Dispose(disposing: false);
        }
    }
} 