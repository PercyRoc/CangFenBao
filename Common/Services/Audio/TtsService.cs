using System.IO;
using System.Speech.Synthesis;
using NAudio.Wave;
using Serilog;

namespace Common.Services.Audio;

/// <summary>
///     文本转语音服务实现（使用NAudio进行音量增强）
/// </summary>
public class TtsService : ITtsService
{
    private readonly SpeechSynthesizer _synthesizer;
    private readonly SemaphoreSlim _speakLock;
    private readonly Dictionary<AudioType, string> _presetTexts;
    private bool _disposed;

    /// <summary>
    ///     构造函数
    /// </summary>
    public TtsService()
    {
        _synthesizer = new SpeechSynthesizer();
        _speakLock = new SemaphoreSlim(1, 1);

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

        _synthesizer.SetOutputToDefaultAudioDevice();
        SetChineseVoice();

        Log.Information("TTS语音服务已初始化 (使用NAudio增强)");
    }

    /// <inheritdoc />
    public async Task<bool> SpeakAsync(string text, int rate = 0, int volume = 100, float volumeMultiplier = 1.0f)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Log.Warning("要播放的文本为空");
            return false;
        }

        Math.Clamp(rate, -10, 10);
        Math.Clamp(volume, 0, 100);
        volumeMultiplier = Math.Max(0.0f, volumeMultiplier);

        if (!await _speakLock.WaitAsync(TimeSpan.FromSeconds(1)))
        {
            Log.Warning("等待语音播放锁超时");
            return false;
        }

        try
        {
            
            var tcs = new TaskCompletionSource<bool>();
            
            // 将合成器输出重定向到内存流
            using var memoryStream = new MemoryStream();
            _synthesizer.SetOutputToWaveStream(memoryStream);
            
            // 异步合成语音
            _synthesizer.SpeakAsync(text);
            
            // 播放完成后的回调
            _synthesizer.SpeakCompleted += async (_, e) =>
            {
                // 确保我们在正确的线程上处理
                await Task.Yield();

                if (e.Error != null)
                {
                    Log.Error(e.Error, "TTS aac synthesis failed");
                    tcs.TrySetResult(false);
                    return;
                }

                try
                {
                    memoryStream.Position = 0;
                    using var waveReader = new WaveFileReader(memoryStream);
                    var sampleProvider = new VolumeSampleProvider(waveReader.ToSampleProvider())
                    {
                        VolumeMultiplier = volumeMultiplier
                    };

                    // 使用 using 确保 WaveOutEvent 被释放
                    using var waveOut = new WaveOutEvent();
                    var playbackFinishedTcs = new TaskCompletionSource<bool>();
                    
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
                    
                    // 等待播放完成
                    var result = await playbackFinishedTcs.Task;
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "NAudio playback preparation failed");
                    tcs.TrySetResult(false);
                }
            };
            
            // 等待整个操作完成
            return await tcs.Task;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "播放TTS语音时发生错误: {Text}", text);
            return false;
        }
        finally
        {
            _synthesizer.SetOutputToDefaultAudioDevice();
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
            _synthesizer.SpeakAsyncCancelAll();
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
            _synthesizer.Pause();
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
            _synthesizer.Resume();
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
            return _synthesizer.GetInstalledVoices().Select(v => v.VoiceInfo);
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
            _synthesizer.SelectVoice(voiceName);
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
            var voices = GetInstalledVoices().ToList();
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
                SetVoice(chineseVoice.Name);
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
            _synthesizer.Dispose();
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
