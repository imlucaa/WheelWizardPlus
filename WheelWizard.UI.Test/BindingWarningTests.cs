using System.Linq.Expressions;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NSubstitute;
using WheelWizard.Models;
using WheelWizard.RrRooms;
using WheelWizard.Shared;
using WheelWizard.Shared.Services;
using WheelWizard.WheelWizardData.ViewModels;
using WheelWizard.WheelWizardData.Views;
using WheelWizard.WheelWizardData.Views.Dialogs;

namespace WheelWizard.UI.Test;

public class BindingWarningTests
{
    [AvaloniaFact]
    public void PlayerProfilePopup_BindsEveryFieldToItself_NotToItsWindow()
    {
        var caller = Substitute.For<IApiCaller<IRwfcApi>>();
        caller
            .CallApiAsync(Arg.Any<Expression<Func<IRwfcApi, Task<PlayerProfileResponse>>>>())
            .Returns(new TaskCompletionSource<OperationResult<PlayerProfileResponse>>().Task);
        caller
            .CallApiAsync(Arg.Any<Expression<Func<IRwfcApi, Task<RwfcPlayerVrHistoryResponse>>>>())
            .Returns(new TaskCompletionSource<OperationResult<RwfcPlayerVrHistoryResponse>>().Task);
        using var model = new VrHistoryViewModel(caller);
        using var warnings = new BindingWarningSink();
        var content = new PlayerProfileWindow(new VrHistoryGraph(model), caller, "1234-5678-9012");
        try
        {
            content.Show();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            content.Close();
        }

        Assert.True(warnings.Broken.Count == 0, string.Join("\n", warnings.Broken));
    }
}
