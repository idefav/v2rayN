using Avalonia.Platform.Storage;
using v2rayN.Desktop.Manager;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Common;

internal class UI
{
    private static readonly string caption = Global.AppName;

    public static async Task<ButtonResult> ShowYesNo(string msg, string? confirmText = null, bool dangerous = false)
    {
        var owner = WindowDialog.TryGetOwnerWindow();
        var box = new MessageBoxDialog(caption, msg, confirmText, dangerous);
        var result = await box.ShowDialog<ButtonResult>(owner);
        return result == ButtonResult.Yes ? ButtonResult.Yes : ButtonResult.No;
    }

    public static async Task<int> ShowUnsavedChanges()
    {
        var dialog = new Window
        {
            Title = Global.AppName, Width = 520, SizeToContent = SizeToContent.Height,
            CanResize = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var buttons = new WrapPanel { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        foreach (var (text, result) in new[] { (WorkspaceText.Stay, 0), (WorkspaceText.Discard, 2), (WorkspaceText.Save, 1) })
        {
            var button = new Button { Content = text, Margin = new Thickness(4), IsDefault = result == 1, IsCancel = result == 0 };
            if (result == 1) button.Classes.Add("primary");
            button.Click += (_, _) => dialog.Close(result);
            buttons.Children.Add(button);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 24,
            Children = { new TextBlock { Text = WorkspaceText.Unsaved, TextWrapping = TextWrapping.Wrap }, buttons }
        };
        return await dialog.ShowDialog<int>(WindowDialog.TryGetOwnerWindow());
    }

    public static async Task<string?> OpenFileDialog(FilePickerFileType? filter)
    {
        var sp = GetStorageProvider();
        if (sp is null)
        {
            return null;
        }

        // Start async operation to open the dialog.
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = filter is null ? [FilePickerFileTypes.All, FilePickerFileTypes.ImagePng] : [filter]
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public static async Task<string?> SaveFileDialog(string filter)
    {
        var sp = GetStorageProvider();
        if (sp is null)
        {
            return null;
        }

        // Start async operation to open the dialog.
        var files = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
        });

        return files?.TryGetLocalPath();
    }

    private static IStorageProvider? GetStorageProvider()
    {
        var owner = WindowDialog.TryGetOwnerWindow();
        var topLevel = TopLevel.GetTopLevel(owner);
        return topLevel?.StorageProvider;
    }
}
