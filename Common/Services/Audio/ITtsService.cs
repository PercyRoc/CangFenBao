// using System.Speech.Synthesis;

namespace Common.Services.Audio;

/// <summary>
///     文本转语音服务接口
/// </summary>
public interface ITtsService : IDisposable
{
    /// <summary>
    ///     播放文本语音
    /// </summary>
    /// <param name="text">要播放的文本</param>
    /// <param name="rate">语音速度 (-10 到 10，默认为 0)</param>
    /// <param name="volume">音量 (0 到 100，默认为 100)</param>
    /// <param name="volumeMultiplier">音量放大倍数 (1.0f 代表正常音量)</param>
    /// <returns>是否播放成功</returns>
    Task<bool> SpeakAsync(string text, int rate = 0, int volume = 100, float volumeMultiplier = 1.0f);

    /// <summary>
    ///     播放预设文本语音
    /// </summary>
    /// <param name="audioType">音频类型</param>
    /// <param name="rate">语音速度 (-10 到 10，默认为 0)</param>
    /// <param name="volume">音量 (0 到 100，默认为 100)</param>
    /// <param name="volumeMultiplier">音量放大倍数 (1.0f 代表正常音量)</param>
    /// <returns>是否播放成功</returns>
    Task<bool> SpeakPresetAsync(AudioType audioType, int rate = 0, int volume = 100, float volumeMultiplier = 1.0f);

    /// <summary>
    ///     停止当前播放
    /// </summary>
    void Stop();

    /// <summary>
    ///     暂停播放
    /// </summary>
    void Pause();

    /// <summary>
    ///     恢复播放
    /// </summary>
    void Resume();

    /// <summary>
    ///     获取可用的语音
    /// </summary>
    /// <returns>可用语音列表</returns>
    // IEnumerable<VoiceInfo> GetInstalledVoices();

    /// <summary>
    ///     设置语音
    /// </summary>
    /// <param name="voiceName">语音名称</param>
    /// <returns>是否设置成功</returns>
    bool SetVoice(string voiceName);
}