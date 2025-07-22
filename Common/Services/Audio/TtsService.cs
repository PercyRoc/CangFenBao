using System.Speech.Synthesis;
using Serilog;

namespace Common.Services.Audio;

/// <summary>
///     文本转语音服务实现
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

        // 初始化预设文本
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

        // 设置默认语音属性
        _synthesizer.Rate = 0;  // 正常语速
        _synthesizer.Volume = 100;  // 最大音量

        // 尝试设置中文语音
        SetChineseVoice();

        Log.Information("TTS语音服务已初始化");
    }

    /// <inheritdoc />
    public async Task<bool> SpeakAsync(string text, int rate = 0, int volume = 100)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Log.Warning("要播放的文本为空");
            return false;
        }

        // 验证参数范围
        rate = Math.Clamp(rate, -10, 10);
        volume = Math.Clamp(volume, 0, 100);

        try
        {
            // 使用信号量确保同一时间只播放一个语音
            if (!await _speakLock.WaitAsync(TimeSpan.FromSeconds(1)))
            {
                Log.Warning("等待语音播放锁超时");
                return false;
            }

            try
            {
                await Task.Run(() =>
                {
                    _synthesizer.Rate = rate;
                    _synthesizer.Volume = volume;
                    _synthesizer.Speak(text);
                });

                Log.Debug("开始播放TTS语音：{Text}", text);
                return true;
            }
            finally
            {
                _speakLock.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "播放TTS语音时发生错误：{Text}", text);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SpeakPresetAsync(AudioType audioType, int rate = 0, int volume = 100)
    {
        if (_presetTexts.TryGetValue(audioType, out var text))
        {
            return await SpeakAsync(text, rate, volume);
        }

        Log.Warning("未找到预设文本：{Type}", audioType);
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
            Log.Information("已设置语音：{VoiceName}", voiceName);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设置语音时发生错误：{VoiceName}", voiceName);
            return false;
        }
    }

    /// <summary>
    ///     设置中文语音
    /// </summary>
    private void SetChineseVoice()
    {
        try
        {
            var voices = GetInstalledVoices().ToList();
            Log.Information("可用语音：{Voices}", string.Join(", ", voices.Select(v => v.Name)));

            // 优先选择中文语音
            var chineseVoice = voices.FirstOrDefault(v => 
                v.Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
                v.Name.Contains("Chinese", StringComparison.OrdinalIgnoreCase) ||
                v.Name.Contains("中文", StringComparison.OrdinalIgnoreCase));

            if (chineseVoice != null)
            {
                SetVoice(chineseVoice.Name);
                Log.Information("已设置中文语音：{VoiceName}", chineseVoice.Name);
            }
            else
            {
                Log.Warning("未找到中文语音，使用默认语音");
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
            _synthesizer.SpeakAsyncCancelAll();
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