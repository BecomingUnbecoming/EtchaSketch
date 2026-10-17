using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using EtchaSketch.UI.ViewModels;

namespace EtchaSketch.UI.Views;

public partial class HomeView : UserControl
{
    // Reminders come due and other devices make changes while the app sits open, so check every minute.
    private readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    public HomeView()
    {
        InitializeComponent();
        _reminderTimer.Tick += (_, _) =>
        {
            (DataContext as HomeViewModel)?.CheckRemindersCommand.Execute(null);
            (DataContext as HomeViewModel)?.AutoSyncCommand.Execute(null);
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as HomeViewModel)?.AutoSyncCommand.Execute(null);
        _reminderTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _reminderTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
