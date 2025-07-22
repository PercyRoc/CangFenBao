using System.IO;
using System.Media;
using Serilog;

namespace Common.Services.Audio;

/// <summary>
///     增强音频服务接口，支持预录制音频和TTS
/// </summary>
public interface IEnhancedAudioService : IAudioService
{
    /// <summary>
    ///     播放TTS语音
    /// </summary>
    /// <param name="text">要播放的文本</param>
    /// <param name="rate">语音速度 (-10 到 10，默认为 0)</param>
    /// <param name="volume">音量 (0 到 100，默认为 100)</param>
    /// <returns>是否播放成功</returns>
    Task<bool> SpeakAsync(string text, int rate = 0, int volume = 100);

    /// <summary>
    ///     播放预设语音（优先使用音频文件，如果不存在则使用TTS）
    /// </summary>
    /// <param name="audioType">音频类型</param>
    /// <param name="forceTts">是否强制使用TTS</param>
    /// <param name="rate">TTS语音速度 (-10 到 10，默认为 0)</param>
    /// <param name="volume">TTS音量 (0 到 100，默认为 100)</param>
    /// <returns>是否播放成功</returns>
    Task<bool> PlayPresetAsync(AudioType audioType, bool forceTts = false, int rate = 0, int volume = 100);

    /// <summary>
    ///     停止所有播放
    /// </summary>
    void StopAll();

    /// <summary>
    ///     设置TTS语音
    /// </summary>
    /// <param name="voiceName">语音名称</param>
    /// <returns>是否设置成功</returns>
    bool SetTtsVoice(string voiceName);

    /// <summary>
    ///     获取可用的TTS语音
    /// </summary>
    /// <returns>可用语音列表</returns>
    IEnumerable<System.Speech.Synthesis.VoiceInfo> GetAvailableTtsVoices();
}

/// <summary>
///     增强音频服务实现，支持预录制音频和TTS
/// </summary>
public class EnhancedAudioService : IEnhancedAudioService, IDisposable
{
    private readonly SoundPlayer _player;
    private readonly ITtsService _ttsService;
    private readonly SemaphoreSlim _playLock;
    private readonly Dictionary<AudioType, string> _presetAudios;
    private readonly string _audioDirectory;
    private bool _disposed;

    /// <summary>
    ///     构造函数
    /// </summary>
    /// <param name="ttsService">TTS服务</param>
    public EnhancedAudioService(ITtsService? ttsService = null)
    {
        _player = new SoundPlayer();
        _ttsService = ttsService ?? new TtsService();
        _playLock = new SemaphoreSlim(1, 1);

        // 初始化音频目录
        _audioDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Audio");
        
        // 初始化预设音频
        _presetAudios = new Dictionary<AudioType, string>
        {
            { AudioType.SystemError, Path.Combine(_audioDirectory, "error.wav") },
            { AudioType.Success, Path.Combine(_audioDirectory, "success.wav") },
            { AudioType.PlcDisconnected, Path.Combine(_audioDirectory, "PLC未连接.wav") },
            { AudioType.WaitingScan, Path.Combine(_audioDirectory, "等待扫码.wav") },
            { AudioType.WaitingForLoading, Path.Combine(_audioDirectory, "等待上包.wav") },
            { AudioType.LoadingTimeout, Path.Combine(_audioDirectory, "超时.wav") },
            { AudioType.LoadingRejected, Path.Combine(_audioDirectory, "拒绝上包.wav") },
            { AudioType.LoadingSuccess, Path.Combine(_audioDirectory, "上包成功.wav") },
            { AudioType.LoadingAllowed, Path.Combine(_audioDirectory, "允许上包.wav") },
            { AudioType.VolumeAbnormal, Path.Combine(_audioDirectory, "体积异常.wav") },
            { AudioType.WeightAbnormal, Path.Combine(_audioDirectory, "重量异常.wav") }
        };

        // 确保音频目录存在
        if (!Directory.Exists(_audioDirectory))
        {
            Directory.CreateDirectory(_audioDirectory);
            Log.Information("创建音频目录：{Directory}", _audioDirectory);
        }

        Log.Information("增强音频服务已初始化，支持预录制音频和TTS");
    }

    /// <inheritdoc />
    public async Task<bool> PlayAsync(string audioPath)
    {
        if (string.IsNullOrEmpty(audioPath))
        {
            Log.Warning("音频文件路径为空");
            return false;
        }

        if (!File.Exists(audioPath))
        {
            Log.Warning("音频文件不存在：{Path}", audioPath);
            return false;
        }

        try
        {
            // 使用信号量确保同一时间只播放一个音频
            if (!await _playLock.WaitAsync(TimeSpan.FromSeconds(1)))
            {
                Log.Warning("等待播放锁超时");
                return false;
            }

            try
            {
                await Task.Run(() =>
                {
                    _player.SoundLocation = audioPath;
                    _player.Load();
                    _player.Play();
                });

                Log.Debug("开始播放音频：{Path}", audioPath);
                return true;
            }
            finally
            {
                _playLock.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "播放音频时发生错误：{Path}", audioPath);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> PlayPresetAsync(AudioType audioType)
    {
        return await PlayPresetAsync(audioType, false, 0, 100);
    }

    /// <inheritdoc />
    public async Task<bool> PlayPresetAsync(AudioType audioType, bool forceTts = false, int rate = 0, int volume = 100)
    {
        // 如果强制使用TTS或音频文件不存在，则使用TTS
        if (forceTts || !_presetAudios.TryGetValue(audioType, out var audioPath) || !File.Exists(audioPath))
        {
            Log.Debug("使用TTS播放预设语音：{Type}", audioType);
            return await _ttsService.SpeakPresetAsync(audioType, rate, volume);
        }

        // 使用预录制音频文件
        Log.Debug("使用预录制音频播放：{Type}", audioType);
        return await PlayAsync(audioPath);
    }

    /// <inheritdoc />
    public async Task<bool> SpeakAsync(string text, int rate = 0, int volume = 100)
    {
        return await _ttsService.SpeakAsync(text, rate, volume);
    }

    /// <inheritdoc />
    public void StopAll()
    {
        try
        {
            _player.Stop();
            _ttsService.Stop();
            Log.Debug("已停止所有音频播放");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止音频播放时发生错误");
        }
    }

    /// <inheritdoc />
    public bool SetTtsVoice(string voiceName)
    {
        return _ttsService.SetVoice(voiceName);
    }

    /// <inheritdoc />
    public IEnumerable<System.Speech.Synthesis.VoiceInfo> GetAvailableTtsVoices()
    {
        return _ttsService.GetInstalledVoices();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            _player.Dispose();
            _ttsService.Dispose();
            _playLock.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放增强音频服务资源时发生错误");
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}