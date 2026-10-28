using Avalonia.Controls;
using WheelWizard.Views.Shell;

namespace WheelWizard.UI.Test;

/// <summary>A bare application window that in-app modals can open on.</summary>
internal sealed class ModalOwnerWindow : BaseWindow
{
    private readonly Border _overlay = new();
    private readonly Border _content = new();
    protected override Control InteractionOverlay => _overlay;
    protected override Control InteractionContent => _content;

    public ModalOwnerWindow()
    {
        Width = 656;
        Height = 876;
        Content = _content;
    }
}
