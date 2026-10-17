using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EtchaSketch.UI.ViewModels;

namespace EtchaSketch.UI.Views;

public partial class MainView : UserControl
{
    // Checks often enough that an unused profile locks within a few seconds of the idle limit.
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    public MainView()
    {
        InitializeComponent();
        _idleTimer.Tick += (_, _) => (DataContext as MainViewModel)?.LockIfIdle();

        // Tunnel, and count handled events too, so every tap and key press anywhere counts as use.
        AddHandler(PointerPressedEvent, OnActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, OnActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnActivity(object? sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.NoteActivity();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _idleTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _idleTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
