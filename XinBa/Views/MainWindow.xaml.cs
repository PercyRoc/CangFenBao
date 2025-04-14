// using System.ComponentModel;
// using System.Windows;
// using System.Windows.Input;
// using Common.Services.Ui;
// using Prism.Ioc;
// using Prism.Services.Dialogs;
// using Serilog;
// using XinBa.Services;
//
// namespace XinBa.Views;
//
// /// <summary>
// ///     Interaction logic for MainWindow.xaml
// /// </summary>
// internal partial class MainWindow
// {
//     private readonly IDialogService _dialogService;
//     private bool _isClosing;
//
//     internal MainWindow(IDialogService dialogService, INotificationService notificationService)
//     {
//         _dialogService = dialogService;
//         InitializeComponent();
//
//         // Register Growl container
//         notificationService.Register("MainWindowGrowl", GrowlPanel);
//
//         // Add title bar mouse event handler
//         MouseDown += OnWindowMouseDown;
//     }
//
//     private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
//     {
//         try
//         {
//             // Allow dragging the window when left button is pressed in the title bar area
//             if (e.ChangedButton == MouseButton.Left && e.GetPosition(this).Y <= 32) DragMove();
//         }
//         catch (Exception ex)
//         {
//             Log.Error(ex, "Error occurred while dragging window");
//         }
//     }
//
//     /// <summary>
//     ///     窗口关闭事件
//     /// </summary>
//     private void MetroWindow_Closing(object sender, CancelEventArgs e)
//     {
//         if (_isClosing) return;
//
//         // 取消当前的关闭操作
//         e.Cancel = true;
//
//         _ = Task.Run(async () =>
//         {
//             try
//             {
//                 _isClosing = true;
//
//                 // 使用已注入的对话框服务
//                 var result = await _dialogService.ShowIconConfirmAsync(
//                     "Are you sure you want to exit the application?",
//                     "Exit Confirmation",
//                     MessageBoxImage.Question);
//
//                 if (result == MessageBoxResult.Yes)
//                 {
//                     // 尝试登出当前用户
//                     try
//                     {
//                         // 获取API服务
//                         var app = Application.Current as App;
//                         var apiService = app?.Container?.Resolve<IApiService>();
//                         if (apiService != null && apiService.IsLoggedIn())
//                         {
//                             Log.Information("窗口关闭前尝试登出用户");
//                             await apiService.LogoutAsync();
//                             Log.Information("用户已登出");
//                         }
//                     }
//                     catch (Exception ex)
//                     {
//                         Log.Error(ex, "窗口关闭前登出用户时发生错误");
//                     }
//
//                     // 关闭窗口
//                     _isClosing = false;
//                     await Dispatcher.InvokeAsync(() =>
//                     {
//                         Close();
//                         Application.Current.Shutdown();
//                     });
//                 }
//                 else
//                 {
//                     _isClosing = false;
//                 }
//             }
//             catch (Exception ex)
//             {
//                 Log.Error(ex, "窗口关闭过程中发生错误");
//                 _isClosing = false;
//                 await Dispatcher.InvokeAsync(() =>
//                 {
//                     Close();
//                     Application.Current.Shutdown();
//                 });
//             }
//         });
//     }
// }