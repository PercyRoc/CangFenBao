using System.IO;
using System.Speech.Synthesis;
using System.Windows.Threading;
using NAudio.Wave;
using Serilog;

namespace Common.Services.Audio;

/// <summary>
///     文本转语音服务实现（使用NAudio进行音量增强）
/// </summary>
public class TtsService : ITtsService
{
    private SpeechSynthesizer? _synthesizer;
    private readonly SemaphoreSlim _speakLock;
    private readonly Dictionary<AudioType, string> _presetTexts;
    private bool _disposed;
    private readonly Dispatcher _dispatcher;
    private bool _isProcessing;
    private DateTime _lastProcessingStart;

    /// <summary>
    ///     构造函数
    /// </summary>
    public TtsService()
    {
        _speakLock = new SemaphoreSlim(1, 1);
        _dispatcher = Dispatcher.CurrentDispatcher;

        _presetTexts = new Dictionary<AudioType, string>
        {
            { AudioType.SystemError, "系统错误" },
            { AudioType.Success, "操作成功" },
            { AudioType.PlcDisconnected, "PLC未连接" },
            { AudioType.WaitingScan, "等待扫码" },
            { AudioType.WaitingForLoading, "等待上包" },
            { AudioType.LoadingTimeout, "超时" },
            { AudioType.LoadingRejected, "拒绝上包" },
            { AudioType.LoadingSuccess, "上包成功" },
            { AudioType.LoadingAllowed, "允许上包" },
            { AudioType.VolumeAbnormal, "体积异常" },
            { AudioType.WeightAbnormal, "重量异常" }
        };

        // 延迟初始化 SpeechSynthesizer，避免线程问题
        InitializeSynthesizer();

        Log.Information("TTS语音服务已初始化 (使用NAudio增强)");
    }

    /// <summary>
    ///     延迟初始化 SpeechSynthesizer，确保在正确的线程中创建
    /// </summary>
    private void InitializeSynthesizer()
    {
        if (_dispatcher.CheckAccess())
        {
            // 在UI线程中，直接创建
            _synthesizer = new SpeechSynthesizer();
            _synthesizer.SetOutputToDefaultAudioDevice();
            SetChineseVoice();
        }
        else
        {
            // 不在UI线程中，通过Dispatcher调度到UI线程
            _dispatcher.Invoke(() =>
            {
                _synthesizer = new SpeechSynthesizer();
                _synthesizer.SetOutputToDefaultAudioDevice();
                SetChineseVoice();
            });
        }
    }

    /// <inheritdoc />
    public async Task<bool> SpeakAsync(string text, int rate = 0, int volume = 100, float volumeMultiplier = 1.0f)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Log.Warning("要播放的文本为空");
            return false;
        }

        rate = Math.Clamp(rate, -10, 10);
        volume = Math.Clamp(volume, 0, 100);
        volumeMultiplier = Math.Max(0.0f, volumeMultiplier);

        // 检查是否正在处理中，如果是则检查是否超时
        if (_isProcessing)
        {
            var processingTime = DateTime.Now - _lastProcessingStart;
            if (processingTime.TotalSeconds > 30) // 30秒超时
            {
                Log.Warning("TTS处理超时 {Seconds} 秒，强制重置状态", processingTime.TotalSeconds);
                _isProcessing = false;
            }
            else
            {
                Log.Debug("TTS正在处理中，跳过本次请求: {Text}", text);
                return false;
            }
        }

        if (!await _speakLock.WaitAsync(TimeSpan.FromSeconds(1)))
        {
            Log.Warning("等待语音播放锁超时");
            return false;
        }

        _isProcessing = true;
        _lastProcessingStart = DateTime.Now;

        try
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var baseStream = new MemoryStream();
            using var nonClosingStream = new NonClosingStreamWrapper(baseStream);

            // 确保 SpeechSynthesizer 操作在正确的线程中执行
            await _dispatcher.InvokeAsync(() =>
            {
                if (_synthesizer == null)
                    throw new InvalidOperationException("SpeechSynthesizer 未初始化");

                _synthesizer.Rate = rate;
                _synthesizer.Volume = volume;
                _synthesizer.SetOutputToWaveStream(nonClosingStream);

                EventHandler<SpeakCompletedEventArgs>? handler = null;
                handler = async (_, e) =>
                {
                    try
                    {
                        // 移除事件处理器前检查对象状态
                        await _dispatcher.InvokeAsync(() =>
                        {
                            if (_synthesizer != null && handler != null)
                            {
                                _synthesizer.SpeakCompleted -= handler;
                            }
                        });
                        await Task.Yield();

                    if (e.Error != null)
                    {
                        Log.Error(e.Error, "TTS synthesis failed");
                        tcs.TrySetResult(false);
                        return;
                    }

                    try
                    {
                        baseStream.Position = 0;
                        using var waveReader = new WaveFileReader(baseStream);
                        var sampleProvider = new VolumeSampleProvider(waveReader.ToSampleProvider())
                        {
                            VolumeMultiplier = volumeMultiplier
                        };

                        using var waveOut = new WaveOutEvent();
                        var playbackFinishedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                        waveOut.PlaybackStopped += (sender, args) =>
                        {
                            if (args.Exception != null)
                            {
                                Log.Error(args.Exception, "NAudio playback failed");
                                playbackFinishedTcs.TrySetResult(false);
                            }
                            else
                            {
                                playbackFinishedTcs.TrySetResult(true);
                            }
                        };

                        waveOut.Init(sampleProvider);
                        waveOut.Play();

                        var result = await playbackFinishedTcs.Task;
                        tcs.TrySetResult(result);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "NAudio playback preparation failed");
                        tcs.TrySetResult(false);
                    }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "TTS事件处理器执行失败");
                        tcs.TrySetResult(false);
                    }
                };

                _synthesizer.SpeakCompleted += handler;
                _synthesizer.SpeakAsync(text);
            });

            return await tcs.Task;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "播放TTS语音时发生错误: {Text}", text);
            return false;
        }
        finally
        {
            try
            {
                _dispatcher.Invoke(() =>
                {
                    if (_synthesizer != null)
                        _synthesizer.SetOutputToDefaultAudioDevice();
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "重置TTS输出设备失败");
            }

            _isProcessing = false;
            _speakLock.Release();
        }
    }


    /// <inheritdoc />
    public async Task<bool> SpeakPresetAsync(AudioType audioType, int rate = 0, int volume = 100, float volumeMultiplier = 1.0f)
    {
        if (_presetTexts.TryGetValue(audioType, out var text))
        {
            return await SpeakAsync(text, rate, volume, volumeMultiplier);
        }

        Log.Warning("未找到预设文本: {Type}", audioType);
        return false;
    }

    /// <inheritdoc />
    public void Stop()
    {
        try
        {
            _dispatcher.Invoke(() =>
            {
                if (_synthesizer != null)
                    _synthesizer.SpeakAsyncCancelAll();
            });
            Log.Debug("已停止TTS语音播放");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止TTS语音播放时发生错误");
        }
    }

    /// <inheritdoc />
    public void Pause()
    {
        try
        {
            _dispatcher.Invoke(() =>
            {
                if (_synthesizer != null)
                    _synthesizer.Pause();
            });
            Log.Debug("已暂停TTS语音播放");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "暂停TTS语音播放时发生错误");
        }
    }

    /// <inheritdoc />
    public void Resume()
    {
        try
        {
            _dispatcher.Invoke(() =>
            {
                if (_synthesizer != null)
                    _synthesizer.Resume();
            });
            Log.Debug("已恢复TTS语音播放");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "恢复TTS语音播放时发生错误");
        }
    }

    /// <inheritdoc />
    public IEnumerable<VoiceInfo> GetInstalledVoices()
    {
        try
        {
            return _dispatcher.Invoke(() =>
                _synthesizer?.GetInstalledVoices().Select(v => v.VoiceInfo) ?? []);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取已安装语音时发生错误");
            return [];
        }
    }

    /// <inheritdoc />
    public bool SetVoice(string voiceName)
    {
        if (string.IsNullOrWhiteSpace(voiceName))
        {
            Log.Warning("语音名称为空");
            return false;
        }

        try
        {
            _dispatcher.Invoke(() =>
            {
                if (_synthesizer != null)
                    _synthesizer.SelectVoice(voiceName);
            });
            Log.Information("已设置语音: {VoiceName}", voiceName);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设置语音时发生错误: {VoiceName}", voiceName);
            return false;
        }
    }

    private void SetChineseVoice()
    {
        try
        {
            var voices = _dispatcher.Invoke(() =>
                _synthesizer?.GetInstalledVoices().Select(v => v.VoiceInfo).ToList() ?? []);

            if (voices.Count == 0)
            {
                Log.Warning("系统中未找到任何TTS语音");
                return;
            }
            Log.Information("可用语音: {Voices}", string.Join(", ", voices.Select(v => v.Name)));

            var chineseVoice = voices.FirstOrDefault(v =>
                v.Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
                v.Name.Contains("Chinese", StringComparison.OrdinalIgnoreCase) ||
                v.Name.Contains("中文", StringComparison.OrdinalIgnoreCase));

            if (chineseVoice != null)
            {
                _dispatcher.Invoke(() =>
                {
                    if (_synthesizer != null)
                        _synthesizer.SelectVoice(chineseVoice.Name);
                });
                Log.Information("已设置中文语音: {VoiceName}", chineseVoice.Name);
            }
            else
            {
                Log.Warning("未找到中文语音，将使用默认语音");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设置中文语音时发生错误");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            _dispatcher.Invoke(() =>
            {
                if (_synthesizer != null)
                    _synthesizer.Dispose();
            });
            _speakLock.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放TTS服务资源时发生错误");
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

