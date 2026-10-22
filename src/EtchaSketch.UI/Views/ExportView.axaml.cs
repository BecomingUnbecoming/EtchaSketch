using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EtchaSketch.UI.ViewModels;

namespace EtchaSketch.UI.Views;

public partial class ExportView : UserControl
{
    public ExportView()
    {
        InitializeComponent();
    }

    private async void Everything_Click(object? sender, RoutedEventArgs e) => await Save(ExportKind.Everything);

    private async void Spreadsheet_Click(object? sender, RoutedEventArgs e) => await Save(ExportKind.Spreadsheet);

    private async void Calendar_Click(object? sender, RoutedEventArgs e) => await Save(ExportKind.Calendar);

    private async Task Save(ExportKind kind)
    {
        if (DataContext is not ExportViewModel export || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var extension = ExportViewModel.Extension(kind);
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the export",
            SuggestedFileName = export.FileName(kind),
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(kind.ToString()) { Patterns = [$"*.{extension}"] }],
        });

        if (file is null)
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(export.Build(kind));
            export.Saved(kind, file.Name);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            export.Failed(exception.Message);
        }
    }
}
