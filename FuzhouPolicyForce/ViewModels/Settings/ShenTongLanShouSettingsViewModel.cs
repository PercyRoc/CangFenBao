using Common.Services.Settings;
using FuzhouPolicyForce.Models;
using FuzhouPolicyForce.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FuzhouPolicyForce.ViewModels.Settings
{
    public class ShenTongLanShouSettingsViewModel : BindableBase
    {
        private readonly ISettingsService _settingsService;
        private readonly ShenTongLanShouService _shenTongService;
        private ShenTongLanShouConfig _config;
        private string _testWaybillNo = "";
        private string _testResult = "";
        private bool _isTestEnabled = true;

        public ShenTongLanShouConfig Config
        {
            get => _config;
            set => SetProperty(ref _config, value);
        }

        public string TestWaybillNo
        {
            get => _testWaybillNo;
            set => SetProperty(ref _testWaybillNo, value);
        }

        public string TestResult
        {
            get => _testResult;
            set => SetProperty(ref _testResult, value);
        }

        public bool IsTestEnabled
        {
            get => _isTestEnabled;
            set => SetProperty(ref _isTestEnabled, value);
        }

        public DelegateCommand SaveCommand { get; }
        public DelegateCommand TestCommand { get; }

        public ShenTongLanShouSettingsViewModel(ISettingsService settingsService, ShenTongLanShouService shenTongService)
        {
            _settingsService = settingsService;
            _shenTongService = shenTongService;
            _config = _settingsService.LoadSettings<ShenTongLanShouConfig>();
            SaveCommand = new DelegateCommand(Save);
            TestCommand = new DelegateCommand(Test);
        }

        private void Save()
        {
            _settingsService.SaveSettings(Config, validate: false);
        }

        private void Test()
        {
            if (string.IsNullOrWhiteSpace(TestWaybillNo))
            {
                TestResult = "请输入测试条码";
                return;
            }

            _ = TestAsync();
        }

        private async Task TestAsync()
        {
            IsTestEnabled = false;
            TestResult = "正在发送测试请求...";

            try
            {
                var request = new ShenTongLanShouRequest
                {
                    Packages = new List<ShenTongPackageDto>
                    {
                        new()
                        {
                            WaybillNo = TestWaybillNo,
                            Weight = "1.00", // 测试默认重量
                            OpTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                        }
                    }
                };

                var response = await _shenTongService.UploadCangKuAutoAsync(request);

                if (response?.Success == true)
                {
                    TestResult = "测试成功！";
                    Log.Information("申通接口测试成功，条码：{WaybillNo}", TestWaybillNo);
                }
                else
                {
                    TestResult = $"测试失败：{response?.ErrorMsg ?? "未知错误"}";
                    Log.Warning("申通接口测试失败，条码：{WaybillNo}，错误：{ErrorMsg}", TestWaybillNo, response?.ErrorMsg);
                }
            }
            catch (Exception ex)
            {
                TestResult = $"测试异常：{ex.Message}";
                Log.Error(ex, "申通接口测试异常，条码：{WaybillNo}", TestWaybillNo);
            }
            finally
            {
                IsTestEnabled = true;
            }
        }
    }
} 