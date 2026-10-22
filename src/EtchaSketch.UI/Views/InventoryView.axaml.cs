using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EtchaSketch.UI.Services;
using EtchaSketch.UI.ViewModels;

namespace EtchaSketch.UI.Views;

public partial class InventoryView : UserControl
{
    public InventoryView()
    {
        InitializeComponent();
    }

    private async void Label_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not InventoryRowViewModel row
            || DataContext is not InventoryViewModel inventory
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the label",
            SuggestedFileName = ItemLabel.FileName(row.Item),
            DefaultExtension = "svg",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("Image (SVG)") { Patterns = ["*.svg"] }],
        });

        if (file is null)
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(ItemLabel.Svg(row.Item));
            inventory.LabelSaved(row.Name, file.Name);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            inventory.LabelFailed(exception.Message);
        }
    }
}
