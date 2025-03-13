using System.IO;
using System.Media;
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
    Success
}

/// <summary>
///     音频服务实现
/// </summary>
public class AudioService : IAudioService, IDisposable
{
    private readonly SoundPlayer _player;
    private readonly SemaphoreSlim _playLock;
    private readonly Dictionary<AudioType, string> _presetAudios;
    private bool _disposed;

    /// <summary>
    ///     构造函数
    /// </summary>
    public AudioService()
    {
        _player = new SoundPlayer();
        _playLock = new SemaphoreSlim(1, 1);

        // 初始化预设音频
        var audioDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Audio");
        _presetAudios = new Dictionary<AudioType, string>
        {
            { AudioType.SystemError, Path.Combine(audioDirectory, "error.wav") },
            { AudioType.Success, Path.Combine(audioDirectory, "biu.wav") }
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

    /// <inheritdoc />
    public async Task<bool> PlayPresetAsync(AudioType audioType)
    {
        if (_presetAudios.TryGetValue(audioType, out var audioPath)) return await PlayAsync(audioPath);
        Log.Warning("未找到预设音频：{Type}", audioType);
        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            _player.Dispose();
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