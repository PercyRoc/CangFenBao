using System;
using System.Threading.Tasks;
using Common.Services.Audio;

namespace AudioTest
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("音频服务测试程序启动...");
            
            // 创建音频服务实例
            IAudioService audioService = new AudioService();
            
            // 显示可用的语音列表
            Console.WriteLine("可用的语音列表:");
            var voices = audioService.GetAvailableVoices();
            foreach (var voice in voices)
            {
                Console.WriteLine($"- {voice}");
            }
            
            // 如果有可用的语音，选择第一个
            if (voices.Count > 0)
            {
                audioService.SetVoice(voices[0]);
                Console.WriteLine($"已选择语音: {voices[0]}");
            }
            
            // 测试中文文本转语音功能
            Console.WriteLine("\n测试中文文本转语音功能...");
            audioService.SetLanguage(Language.Chinese);
            Console.WriteLine($"当前语言: {audioService.GetCurrentLanguage()}");
            
            // 测试默认参数（已修改为语速3，音量100）
            Console.WriteLine("使用默认参数（语速3，音量100）...");
            await audioService.SpeakTextAsync("这是默认参数测试，语速已提高，文本已精简");
            
            // 测试不同语速
            Console.WriteLine("\n测试不同语速...");
            Console.WriteLine("语速1...");
            await audioService.SpeakTextAsync("这是语速1的测试", 1);
            
            Console.WriteLine("语速5...");
            await audioService.SpeakTextAsync("这是语速5的测试", 5);
            
            // 测试不同音量
            Console.WriteLine("\n测试不同音量...");
            Console.WriteLine("音量70...");
            await audioService.SpeakTextAsync("这是音量70的测试", 3, 70);
            
            Console.WriteLine("音量100...");
            await audioService.SpeakTextAsync("这是音量100的测试", 3, 100);
            
            // 测试中文预设音频类型
            Console.WriteLine("\n测试中文预设音频类型...");
            Console.WriteLine("播放 ApiError 音频...");
            await audioService.PlayPresetAsync(AudioType.ApiError);
            
            Console.WriteLine("播放 NetworkError 音频...");
            await audioService.PlayPresetAsync(AudioType.NetworkError);
            
            Console.WriteLine("播放 DataError 音频...");
            await audioService.PlayPresetAsync(AudioType.DataError);
            
            Console.WriteLine("播放 ServerError 音频...");
            await audioService.PlayPresetAsync(AudioType.ServerError);
            
            // 切换到英文
            Console.WriteLine("\n切换到英文...");
            audioService.SetLanguage(Language.English);
            Console.WriteLine($"当前语言: {audioService.GetCurrentLanguage()}");
            
            // 测试英文文本转语音功能
            Console.WriteLine("\n测试英文文本转语音功能...");
            
            // 测试默认参数（已修改为语速3，音量100）
            Console.WriteLine("使用默认参数（语速3，音量100）...");
            await audioService.SpeakTextAsync("This is a test with default parameters, faster speed and simplified text");
            
            // 测试不同语速
            Console.WriteLine("\n测试不同语速...");
            Console.WriteLine("语速1...");
            await audioService.SpeakTextAsync("This is speed 1 test", 1);
            
            Console.WriteLine("语速5...");
            await audioService.SpeakTextAsync("This is speed 5 test", 5);
            
            // 测试不同音量
            Console.WriteLine("\n测试不同音量...");
            Console.WriteLine("音量70...");
            await audioService.SpeakTextAsync("This is volume 70 test", 3, 70);
            
            Console.WriteLine("音量100...");
            await audioService.SpeakTextAsync("This is volume 100 test", 3, 100);
            
            // 测试英文预设音频类型
            Console.WriteLine("\n测试英文预设音频类型...");
            Console.WriteLine("播放 ApiError 音频...");
            await audioService.PlayPresetAsync(AudioType.ApiError);
            
            Console.WriteLine("播放 NetworkError 音频...");
            await audioService.PlayPresetAsync(AudioType.NetworkError);
            
            Console.WriteLine("播放 DataError 音频...");
            await audioService.PlayPresetAsync(AudioType.DataError);
            
            Console.WriteLine("播放 ServerError 音频...");
            await audioService.PlayPresetAsync(AudioType.ServerError);
            
            Console.WriteLine("\n测试完成，按任意键退出...");
            Console.ReadKey();
            
            // 释放资源
            (audioService as IDisposable)?.Dispose();
        }
    }
}