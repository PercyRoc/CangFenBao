using Common.Services.Ui;
using Prism.Services.Dialogs;
using Serilog;
using ShanghaiFaxunLogistics.Models.ASN;
using ShanghaiFaxunLogistics.ViewModels;
using System.Windows;

namespace ShanghaiFaxunLogistics.Services.ASN
{
    /// <summary>
    /// ASN服务实现
    /// </summary>
    public class AsnService(
        IDialogService dialogService,
        INotificationService notificationService,
        MainWindowViewModel? mainWindowViewModel = null)
        : IAsnService
    {
        /// <summary>
        /// 处理ASN单数据
        /// </summary>
        public Response ProcessAsnOrderInfo(AsnOrderInfo asnInfo)
        {
            try
            {
                Log.Information("收到ASN单数据: {@AsnInfo}", new
                {
                    asnInfo.OrderCode, 
                    asnInfo.CarCode,
                    ItemsCount = asnInfo.Items.Count
                });

                // 如果ViewModel不存在，则无法执行确认和缓存，直接返回成功
                if (mainWindowViewModel == null)
                {
                    Log.Warning("MainWindowViewModel不可用，无法缓存ASN订单数据");
                    return Response.CreateSuccess();
                }

                // 使用UI线程显示确认对话框
                var confirmed = false;
                
                // 必须在UI线程上弹出对话框
                Application.Current.Dispatcher.Invoke(() =>
                {
                    // 显示确认对话框
                    var parameters = new DialogParameters
                    {
                        { "AsnOrderInfo", asnInfo }
                    };

                    dialogService.ShowDialog("AsnOrderConfirmDialog", parameters, result =>
                    {
                        if (result.Result == ButtonResult.OK && result.Parameters.ContainsKey("Confirmed"))
                        {
                            confirmed = result.Parameters.GetValue<bool>("Confirmed");
                            
                            if (confirmed)
                            {
                                // 确认后缓存数据
                                mainWindowViewModel.CacheAsnOrderInfo(asnInfo);
                                notificationService.ShowSuccess($"已确认ASN单：{asnInfo.OrderCode}");
                            }
                            else
                            {
                                notificationService.ShowWarning($"已取消ASN单：{asnInfo.OrderCode}");
                            }
                        }
                        else
                        {
                            // 取消
                            notificationService.ShowWarning($"已取消ASN单：{asnInfo.OrderCode}");
                        }
                    });
                });

                return confirmed ? Response.CreateSuccess() : Response.CreateFailed("用户取消了ASN单确认", "USER_CANCELLED");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理ASN单数据失败: {OrderCode}", asnInfo.OrderCode);
                return Response.CreateFailed($"处理ASN单数据失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理扫码复核请求
        /// </summary>
        public Response ProcessMaterialReview(MaterialReviewRequest request)
        {
            try
            {
                Log.Information("收到扫码复核请求: {@Request}", new 
                { 
                    request.BoxCode, 
                    request.ExitArea 
                });

                // TODO: 这里添加实际业务处理逻辑
                // 例如：验证箱号、更新状态等

                return Response.CreateSuccess();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理扫码复核请求失败: {BoxCode}", request.BoxCode);
                return Response.CreateFailed($"处理扫码复核请求失败: {ex.Message}");
            }
        }
    }
} 