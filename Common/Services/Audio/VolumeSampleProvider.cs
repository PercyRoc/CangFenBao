
using NAudio.Wave;

namespace Common.Services.Audio;

/// <summary>
///     一个ISampleProvider实现，允许动态调整音量
/// </summary>
/// <remarks>
///     构造函数
/// </remarks>
/// <param name="sourceProvider">源ISampleProvider</param>
public class VolumeSampleProvider(ISampleProvider sourceProvider) : ISampleProvider
{
    private const float Tolerance = 0.0001f; // 小的容差值
    private readonly float _volumeMultiplier = 1.0f;

    /// <summary>
    ///     音量乘数 (1.0f = 100% volume)
    ///     安全范围：0.0f - 5.0f
    ///     建议范围：0.5f - 2.0f
    /// </summary>
    public float VolumeMultiplier
    {
        get => _volumeMultiplier;
        init
        {
            _volumeMultiplier = value switch
            {
                // 限制音量倍数在安全范围内
                < 0.0f => 0.0f,
                > 5.0f => 5.0f,
                _ => value
            };
        }
    }

    /// <inheritdoc />
    public WaveFormat WaveFormat => sourceProvider.WaveFormat;

    /// <inheritdoc />
    public int Read(float[] buffer, int offset, int count)
    {
        var samplesRead = sourceProvider.Read(buffer, offset, count);

        // 使用容差值进行浮点数比较
        if (!(Math.Abs(VolumeMultiplier - 1.0f) > Tolerance)) return samplesRead;
        for (var i = 0; i < samplesRead; i++)
        {
            var sample = buffer[offset + i] * VolumeMultiplier;

            sample = sample switch
            {
                // 防止削波（Clipping）
                > 1.0f => 1.0f,
                < -1.0f => -1.0f,
                _ => sample
            };

            buffer[offset + i] = sample;
        }

        return samplesRead;
    }
}
