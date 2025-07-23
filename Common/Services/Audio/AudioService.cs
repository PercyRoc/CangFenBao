using System.IO;
using System.Media;
using System.Speech.Synthesis;
using Serilog;

namespace Common.Services.Audio;

/// <summary>
///     音频服务接口
/// </summary>
public interface IAudioService
{
    /// <summary>
    ///     播放音频文件
    /// </summary>
    /// <param name="audioPath">音频文件路径</param>
    /// <returns>是否播放成功</returns>
    Task<bool> PlayAsync(string audioPath);

    /// <summary>
    ///     播放预设音频
    /// </summary>
    /// <param name="audioType">音频类型</param>
    /// <returns>是否播放成功</returns>
    Task<bool> PlayPresetAsync(AudioType audioType);
    
    /// <summary>
    ///     播放文本（文本转语音）
    /// </summary>
    /// <param name="text">要播放的文本内容</param>
    /// <param name="rate">语速（-10到10，0为正常速度）</param>
    /// <param name="volume">音量（0到100）</param>
    /// <returns>是否播放成功</returns>
    Task<bool> SpeakTextAsync(string text, int rate = 0, int volume = 100);
    
    /// <summary>
    ///     获取可用的语音列表
    /// </summary>
    /// <returns>语音列表</returns>
    IReadOnlyList<string> GetAvailableVoices();
    
    /// <summary>
    ///     设置语音
    /// </summary>
    /// <param name="voiceName">语音名称</param>
    /// <returns>是否设置成功</returns>
    bool SetVoice(string voiceName);
    
    /// <summary>
    ///     设置语言
    /// </summary>
    /// <param name="language">语言类型</param>
    void SetLanguage(Language language);
    
    /// <summary>
    ///     获取当前语言
    /// </summary>
    /// <returns>当前语言类型</returns>
    Language GetCurrentLanguage();
}

/// <summary>
///     预设音频类型
/// </summary>
public enum AudioType
{
    /// <summary>
    ///     系统错误
    /// </summary>
    SystemError,

    /// <summary>
    ///     通用成功音效
    /// </summary>
    Success,
    
    /// <summary>
    ///     PLC未连接
    /// </summary>
    PlcDisconnected,
    
    /// <summary>
    ///     等待扫码
    /// </summary>
    WaitingScan,
    
    /// <summary>
    ///     等待上包
    /// </summary>
    WaitingForLoading,
    
    /// <summary>
    ///     上包超时
    /// </summary>
    LoadingTimeout,
    
    /// <summary>
    ///     拒绝上包
    /// </summary>
    LoadingRejected,
    
    /// <summary>
    ///     上包成功
    /// </summary>
    LoadingSuccess,

    /// <summary>
    ///     体积异常
    /// </summary>
    VolumeAbnormal,

    /// <summary>
    ///     重量异常
    /// </summary>
    WeightAbnormal,

    /// <summary>
    ///     离开提示音
    /// </summary>
    Leave,
    
    /// <summary>
    ///     API错误（文本转语音）
    /// </summary>
    ApiError,
    
    /// <summary>
    ///     网络错误（文本转语音）
    /// </summary>
    NetworkError,
    
    /// <summary>
    ///     数据异常（文本转语音）
    /// </summary>
    DataError,
    
    /// <summary>
    ///     服务器错误（文本转语音）
    /// </summary>
    ServerError
}

/// <summary>
///     语言类型
/// </summary>
public enum Language
{
    /// <summary>
    ///     中文
    /// </summary>
    Chinese,
    
    /// <summary>
    ///     英文
    /// </summary>
    English
}

/// <summary>
///     音频服务实现
/// </summary>
public class AudioService : IAudioService, IDisposable
{
    private readonly SoundPlayer _player;
    private readonly SpeechSynthesizer _speechSynthesizer;
    private readonly SemaphoreSlim _playLock;
    private readonly Dictionary<AudioType, string> _presetAudios;
    private bool _disposed;

    /// <summary>
    ///     构造函数
    /// </summary>
    public AudioService()
    {
        _player = new SoundPlayer();
        _speechSynthesizer = new SpeechSynthesizer();
        _speechSynthesizer.SetOutputToDefaultAudioDevice();
        _playLock = new SemaphoreSlim(1, 1);

        // 初始化预设音频
        var audioDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Audio");
        _presetAudios = new Dictionary<AudioType, string>
        {
            { AudioType.SystemError, Path.Combine(audioDirectory, "error.wav") },
            { AudioType.Success, Path.Combine(audioDirectory, "success.wav") },
            { AudioType.PlcDisconnected, Path.Combine(audioDirectory, "PLC未连接.wav") },
            { AudioType.WaitingScan, Path.Combine(audioDirectory, "等待扫码.wav") },
            { AudioType.WaitingForLoading, Path.Combine(audioDirectory, "等待上包.wav") },
            { AudioType.LoadingTimeout, Path.Combine(audioDirectory, "超时.wav") },
            { AudioType.LoadingRejected, Path.Combine(audioDirectory, "拒绝上包.wav") },
            { AudioType.LoadingSuccess, Path.Combine(audioDirectory, "上包成功.wav") },
            { AudioType.VolumeAbnormal, Path.Combine(audioDirectory, "体积异常.wav") },
            { AudioType.WeightAbnormal, Path.Combine(audioDirectory, "重量异常.wav") },
            { AudioType.Leave, Path.Combine(audioDirectory, "leave.wav") }
        };

        // 确保音频目录存在
        if (Directory.Exists(audioDirectory)) return;
        Directory.CreateDirectory(audioDirectory);
        Log.Information("创建音频目录：{Directory}", audioDirectory);
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

    /// <summary>
    ///     预设音频类型对应的中文文本内容
    /// </summary>
    private readonly Dictionary<AudioType, string> _presetTextsChinese = new()
    {
        { AudioType.SystemError, "系统错误" },
        { AudioType.Success, "成功" },
        { AudioType.PlcDisconnected, "PLC未连接" },
        { AudioType.WaitingScan, "请扫码" },
        { AudioType.WaitingForLoading, "请放包裹" },
        { AudioType.LoadingTimeout, "上包超时" },
        { AudioType.LoadingRejected, "拒绝上包" },
        { AudioType.LoadingSuccess, "上包成功" },
        { AudioType.VolumeAbnormal, "体积异常" },
        { AudioType.WeightAbnormal, "重量异常" },
        { AudioType.Leave, "请取包裹" },
        { AudioType.ApiError, "API错误" },
        { AudioType.NetworkError, "网络错误" },
        { AudioType.DataError, "数据异常" },
        { AudioType.ServerError, "服务器错误" }
    };
    
    /// <summary>
    ///     预设音频类型对应的英文文本内容
    /// </summary>
    private readonly Dictionary<AudioType, string> _presetTextsEnglish = new()
    {
        { AudioType.SystemError, "System error" },
        { AudioType.Success, "Success" },
        { AudioType.PlcDisconnected, "PLC disconnected" },
        { AudioType.WaitingScan, "Scan barcode" },
        { AudioType.WaitingForLoading, "Place package" },
        { AudioType.LoadingTimeout, "Loading timeout" },
        { AudioType.LoadingRejected, "Loading rejected" },
        { AudioType.LoadingSuccess, "Loading success" },
        { AudioType.VolumeAbnormal, "Volume error" },
        { AudioType.WeightAbnormal, "Weight error" },
        { AudioType.Leave, "Take package" },
        { AudioType.ApiError, "API error" },
        { AudioType.NetworkError, "Network error" },
        { AudioType.DataError, "Data error" },
        { AudioType.ServerError, "Server error" }
    };
    
    /// <summary>
    ///     当前使用的语言
    /// </summary>
    private Language _currentLanguage = Language.Chinese;
    
    /// <inheritdoc />
    public async Task<bool> PlayPresetAsync(AudioType audioType)
    {
        // 首先尝试播放预设音频文件
        if (_presetAudios.TryGetValue(audioType, out var audioPath) && File.Exists(audioPath))
        {
            return await PlayAsync(audioPath);
        }
        
        // 如果音频文件不存在，尝试使用文本到语音功能
        string text = null;
        
        // 根据当前语言选择相应的文本
        switch (_currentLanguage)
        {
            case Language.Chinese:
                _presetTextsChinese.TryGetValue(audioType, out text);
                break;
            case Language.English:
                _presetTextsEnglish.TryGetValue(audioType, out text);
                break;
            default:
                _presetTextsChinese.TryGetValue(audioType, out text);
                break;
        }
        
        if (!string.IsNullOrEmpty(text))
        {
            Log.Information("预设音频文件不存在，使用{Language}文本到语音功能：{Type}", _currentLanguage, audioType);
            return await SpeakTextAsync(text);
        }
        
        Log.Warning("未找到预设音频或文本：{Type}", audioType);
        return false;
    }
    
    /// <inheritdoc />
    public void SetLanguage(Language language)
    {
        _currentLanguage = language;
        Log.Information("已设置语言为：{Language}", language);
    }
    
    /// <inheritdoc />
    public Language GetCurrentLanguage()
    {
        return _currentLanguage;
    }

    /// <inheritdoc />
    public async Task<bool> SpeakTextAsync(string text, int rate = 3, int volume = 100)
    {
        if (string.IsNullOrEmpty(text))
        {
            Log.Warning("要播放的文本为空");
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
                    // 设置语速和音量
                    _speechSynthesizer.Rate = Math.Clamp(rate, -10, 10);
                    _speechSynthesizer.Volume = Math.Clamp(volume, 0, 100);
                    
                    // 播放文本
                    _speechSynthesizer.Speak(text);
                });

                Log.Debug("开始播放文本：{Text}", text);
                return true;
            }
            finally
            {
                _playLock.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "播放文本时发生错误：{Text}", text);
            return false;
        }
    }
    
    /// <inheritdoc />
    public IReadOnlyList<string> GetAvailableVoices()
    {
        try
        {
            return _speechSynthesizer.GetInstalledVoices()
                .Where(v => v.Enabled)
                .Select(v => v.VoiceInfo.Name)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取可用语音列表时发生错误");
            return Array.Empty<string>();
        }
    }
    
    /// <inheritdoc />
    public bool SetVoice(string voiceName)
    {
        if (string.IsNullOrEmpty(voiceName))
        {
            Log.Warning("语音名称为空");
            return false;
        }
        
        try
        {
            // 检查语音是否存在
            var voices = _speechSynthesizer.GetInstalledVoices()
                .Where(v => v.Enabled)
                .Select(v => v.VoiceInfo.Name)
                .ToList();
                
            if (!voices.Contains(voiceName))
            {
                Log.Warning("未找到指定的语音：{VoiceName}", voiceName);
                return false;
            }
            
            // 设置语音
            _speechSynthesizer.SelectVoice(voiceName);
            Log.Debug("已设置语音：{VoiceName}", voiceName);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设置语音时发生错误：{VoiceName}", voiceName);
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            _player.Dispose();
            _speechSynthesizer.Dispose();
            _playLock.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放音频服务资源时发生错误");
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}