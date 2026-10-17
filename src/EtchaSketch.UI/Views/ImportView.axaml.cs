using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EtchaSketch.UI.ViewModels;

namespace EtchaSketch.UI.Views;

public partial class ImportView : UserControl
{
    private const long MaxFileBytes = 10 * 1024 * 1024;

    public ImportView()
    {
        InitializeComponent();
    }

    private async void ChooseFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ImportViewModel import || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a task file",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Task file") { Patterns = ["*.json"] }, FilePickerFileTypes.All],
        });

        if (files.Count == 0)
        {
            return;
        }

        await using var stream = await files[0].OpenReadAsync();
        if (stream.CanSeek && stream.Length > MaxFileBytes)
        {
            import.FileText = "";
            return;
        }

        using var reader = new StreamReader(stream);
        import.FileText = await reader.ReadToEndAsync();
    }
}
