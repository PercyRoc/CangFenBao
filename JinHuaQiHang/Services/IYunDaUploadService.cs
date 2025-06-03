using System.Threading.Tasks;
using JinHuaQiHang.Models.Api;

namespace JinHuaQiHang.Services
{
    public interface IYunDaUploadService
    {
        Task<YunDaUploadResult> UploadPackageInfoAsync(Common.Models.Package.PackageInfo packageInfo);
    }
} 