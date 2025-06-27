# ChileSowing 双语化配置说明

## 概述
ChileSowing（智利播种墙）项目已完成双语化配置，支持中文（简体）和英文两种语言。用户可以在运行时动态切换语言，无需重启应用程序。

## 技术实现
- **本地化框架**: WPFLocalizeExtension
- **资源文件**: 基于 .resx 文件的资源管理
- **语言切换**: 实时动态切换，使用 LocalizeDictionary
- **配置持久化**: 通过 SettingsService 保存用户语言偏好

## 支持的语言
- **英文 (en-US)**: English
- **简体中文 (zh-CN)**: 简体中文

## 使用方法

### 1. 运行时语言切换
在主界面顶部的标题栏中，点击"Language/语言"按钮，在下拉菜单中选择所需语言：
- **English**: 切换到英文界面
- **简体中文**: 切换到中文界面

### 2. 默认语言设置
应用程序默认启动语言为英文（en-US）。用户首次切换语言后，系统会记住该设置，下次启动时自动应用上次选择的语言。

### 3. 语言配置存储
语言设置存储在应用程序配置文件中，位置：
```
Settings/LanguageSettings.json
```

## 开发指南

### 1. 资源文件结构
```
ChileSowing/Resources/
├── Strings.resx          // 默认资源文件（中文）
├── Strings.en-us.resx    // 英文资源文件
└── Strings.Designer.cs   // 自动生成的代码文件
```

### 2. 添加新的本地化字符串
要添加新的本地化字符串，需要：

1. **在 Strings.resx 中添加中文文本**：
   ```xml
   <data name="New_Key" xml:space="preserve">
       <value>中文文本</value>
   </data>
   ```

2. **在 Strings.en-us.resx 中添加对应的英文文本**：
   ```xml
   <data name="New_Key" xml:space="preserve">
       <value>English Text</value>
   </data>
   ```

3. **在 XAML 中使用本地化标记**：
   ```xml
   <TextBlock Text="{lex:Loc New_Key}" />
   ```

### 3. XAML 配置要求
在需要使用本地化的 XAML 文件中，必须包含以下命名空间声明：
```xml
xmlns:lex="http://wpflocalizeextension.codeplex.com"
```

### 4. 服务注册
语言服务已在 App.xaml.cs 中注册：
```csharp
containerRegistry.RegisterSingleton<ILanguageService, LanguageService>();
```

### 5. 在 ViewModel 中使用语言服务
```csharp
public class MyViewModel
{
    private readonly ILanguageService _languageService;
    
    public MyViewModel(ILanguageService languageService)
    {
        _languageService = languageService;
        ChangeLanguageCommand = new DelegateCommand<string>(ExecuteChangeLanguage);
    }
    
    private void ExecuteChangeLanguage(string languageCode)
    {
        _languageService.ChangeLanguage(languageCode);
    }
}
```

## 已本地化的界面元素

### 主界面 (MainWindow)
- 窗口标题
- 设置按钮
- 历史按钮  
- 语言菜单
- SKU 输入标签
- 波次号输入标签
- 包裹信息面板
- 统计信息面板
- 格口监控标题
- 状态栏信息

### 设置对话框 (SettingsDialog)
- TCP Modbus 设置
- 格口规则设置
- 保存/取消按钮

### 格口详情对话框 (ChuteDetailDialog)
- 关闭按钮

## 注意事项

1. **资源文件编辑**: 建议使用 Visual Studio 的资源编辑器或 ResX Editor 工具编辑资源文件。

2. **键名规范**: 资源键名建议使用有意义的前缀，如：
   - `Window_` : 窗口相关
   - `Button_` : 按钮文本
   - `Label_` : 标签文本
   - `Status_` : 状态信息
   - `Tooltip_` : 工具提示

3. **测试**: 添加新字符串后，需要在两种语言下测试界面显示效果。

4. **性能**: WPFLocalizeExtension 在语言切换时会自动更新所有绑定的界面元素，无需手动刷新。

## 故障排除

### 1. 界面文本未更新
- 检查资源文件中是否存在对应的键
- 确认 XAML 中的本地化标记语法正确
- 重新编译项目

### 2. 语言切换无效
- 检查 LanguageService 是否正确注册
- 确认 App.xaml 中的 WPFLocalizeExtension 配置正确
- 查看日志中的错误信息

### 3. 资源文件编译错误
- 确保 .resx 文件的 Build Action 设置为 "Embedded Resource"
- 检查资源文件的 XML 格式是否正确

## 扩展支持更多语言

要添加新语言支持（如日语 ja-JP），需要：

1. 创建新的资源文件：`Strings.ja-jp.resx`
2. 在 `LanguageService` 中添加语言支持：
   ```csharp
   public Dictionary<string, string> SupportedLanguages => new()
   {
       { "en-US", "English" },
       { "zh-CN", "简体中文" },
       { "ja-JP", "日本語" }  // 新增
   };
   ```
3. 在主界面的语言菜单中添加对应的菜单项。

## 总结
ChileSowing 项目的双语化配置遵循 WPF 最佳实践，提供了完整的多语言支持框架。开发者可以轻松添加新的本地化字符串，用户可以方便地切换界面语言。 