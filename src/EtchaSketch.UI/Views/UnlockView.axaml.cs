using Avalonia;
using Avalonia.Controls;

namespace EtchaSketch.UI.Views;

public partial class UnlockView : UserControl
{
    public UnlockView()
    {
        InitializeComponent();
    }

    // Ready to type straight away, without tapping the box first.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        PinBox.Focus();
    }
}
