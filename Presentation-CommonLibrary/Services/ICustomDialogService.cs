using Prism.Services.Dialogs;

namespace Presentation_CommonLibrary.Services;

public interface ICustomDialogService
{
    void ShowDialog(string name, IDialogParameters? parameters = null, Action<IDialogResult>? callback = null);
}