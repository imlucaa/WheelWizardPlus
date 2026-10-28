using System.ComponentModel;
using Avalonia;
using Avalonia.Data;
using Avalonia.Threading;
using WheelWizard.Models;
using WheelWizard.RrRooms;
using WheelWizard.Shared.Services;
using WheelWizard.Views.Dialogs.Base;
using WheelWizard.WheelWizardData.Views;
using WheelWizard.WiiManagement.MiiManagement;
using WheelWizard.WiiManagement.MiiManagement.Domain.Mii;

namespace WheelWizard.WheelWizardData.Views.Dialogs;

public partial class PlayerProfileWindow : PopupContent, INotifyPropertyChanged
{
    private IApiCaller<IRwfcApi> ApiCaller { get; }

    private PlayerProfileResponse? _profile;
    private bool _isLoading = true;
    private string _errorMessage = string.Empty;

    public PlayerProfileWindow(VrHistoryGraph historyGraph, IApiCaller<IRwfcApi> apiCaller, string friendCode)
        : base(true, true, false, "Player Profile")
    {
        ApiCaller = apiCaller;
        // Set before the XAML builds its bindings: the base popup is already inside its window by now, so they would first
        // bind to that window (and log a failed cast for every field) before this line ran.
        DataContext = this;
        InitializeComponent();
        HistoryHost.Content = historyGraph;
        historyGraph.Bind(VrHistoryGraph.FriendCodeProperty, new Binding(nameof(ProfileFriendCode)) { Source = this });
        Window.WindowTitle = friendCode;
        LoadProfile(friendCode);
    }

    public PlayerProfileResponse? Profile
    {
        get => _profile;
        set
        {
            _profile = value;
            OnPropertyChanged(nameof(Profile));
            OnPropertyChanged(nameof(ProfileName));
            OnPropertyChanged(nameof(ProfileFriendCode));
            OnPropertyChanged(nameof(IsSuspicious));
            OnPropertyChanged(nameof(DisplayMii));
            OnPropertyChanged(nameof(LastSeenText));
            OnPropertyChanged(nameof(Vr24HoursText));
            OnPropertyChanged(nameof(VrWeekText));
            OnPropertyChanged(nameof(VrMonthText));
            OnPropertyChanged(nameof(RankText));
            OnPropertyChanged(nameof(VrText));
            OnPropertyChanged(nameof(Vr24Brush));
            OnPropertyChanged(nameof(VrWeekBrush));
            OnPropertyChanged(nameof(VrMonthBrush));
        }
    }

    // The page binds to these, not to Profile.Name and so on: before the profile has loaded the latter walk through a null
    // and log a warning for each field.
    public string ProfileName => Profile?.Name ?? string.Empty;

    public string? ProfileFriendCode => Profile?.FriendCode;

    public bool IsSuspicious => Profile?.IsSuspicious ?? false;

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            _isLoading = value;
            OnPropertyChanged(nameof(IsLoading));
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            _errorMessage = value;
            OnPropertyChanged(nameof(ErrorMessage));
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public string Vr24HoursText => Profile?.VrStats == null ? "--" : FormatSignedValue(Profile.VrStats.Last24Hours);

    public string VrWeekText => Profile?.VrStats == null ? "--" : FormatSignedValue(Profile.VrStats.LastWeek);

    public string VrMonthText => Profile?.VrStats == null ? "--" : FormatSignedValue(Profile.VrStats.LastMonth);

    public string RankText => Profile?.Rank > 0 ? $"#{Profile.Rank:N0}" : "—";

    public string VrText => Profile == null ? "--" : Profile.Vr.ToString("N0");

    public Avalonia.Media.IBrush Vr24Brush => GainBrush(Profile?.VrStats?.Last24Hours);

    public Avalonia.Media.IBrush VrWeekBrush => GainBrush(Profile?.VrStats?.LastWeek);

    public Avalonia.Media.IBrush VrMonthBrush => GainBrush(Profile?.VrStats?.LastMonth);

    private static Avalonia.Media.IBrush GainBrush(int? value) =>
        value > 0 ? Avalonia.Media.Brushes.MediumSeaGreen
        : value < 0 ? Avalonia.Media.Brushes.IndianRed
        : Avalonia.Media.Brushes.Gray;

    public Mii? DisplayMii
    {
        get
        {
            if (Profile?.MiiData == null)
                return null;

            try
            {
                var result = MiiSerializer.Deserialize(Profile.MiiData);
                return result.IsSuccess ? result.Value : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public string LastSeenText
    {
        get
        {
            if (Profile == null)
                return string.Empty;

            var timeAgo = DateTime.UtcNow - Profile.LastSeen;
            if (timeAgo.TotalDays >= 1)
                return $"{(int)timeAgo.TotalDays} day(s) ago";
            if (timeAgo.TotalHours >= 1)
                return $"{(int)timeAgo.TotalHours} hour(s) ago";
            if (timeAgo.TotalMinutes >= 1)
                return $"{(int)timeAgo.TotalMinutes} minute(s) ago";
            return "Just now";
        }
    }

    private async void LoadProfile(string friendCode)
    {
        IsLoading = true;
        ErrorMessage = string.Empty;

        var result = await ApiCaller.CallApiAsync(rwfcApi => rwfcApi.GetPlayerProfileAsync(friendCode));

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (result.IsSuccess && result.Value != null)
            {
                Profile = result.Value;
                Window.WindowTitle = Profile.Name;
                IsLoading = false;
            }
            else
            {
                ErrorMessage = result.Error?.Message ?? "Failed to load profile";
                IsLoading = false;
            }
        });
    }

    private static string FormatSignedValue(int value)
    {
        if (value > 0)
            return $"+{value:N0}";

        return value.ToString("N0");
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
