using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EtchaSketch.UI.ViewModels;

namespace EtchaSketch.UI.Views;

public partial class ThingsImportView : UserControl
{
    private const long MaxFileBytes = 10 * 1024 * 1024;

    public ThingsImportView()
    {
        InitializeComponent();
    }

    private async void ChooseFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ThingsImportViewModel import || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the exported file",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("CSV or TSV") { Patterns = ["*.csv", "*.tsv", "*.txt"] }, FilePickerFileTypes.All],
        });

        if (files.Count == 0)
        {
            return;
        }

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            if (stream.CanSeek && stream.Length > MaxFileBytes)
            {
                import.FileName = $"{files[0].Name} is too big (over 10 MB).";
                import.FileText = "";
                return;
            }

            using var reader = new StreamReader(stream);
            import.FileText = await reader.ReadToEndAsync();
            import.FileName = files[0].Name;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            import.FileName = $"Couldn't read it: {exception.Message}";
        }
    }
}
