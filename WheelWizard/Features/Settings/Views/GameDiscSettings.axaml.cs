using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WheelWizard.DiscImages;
using WheelWizard.GameDiscs;
using WheelWizard.Models.Enums;
using WheelWizard.Shared.Desktop.Storage;
using WheelWizard.Views.Components;
using WheelWizard.Views.Shell;
using WheelWizard.WiiManagement.GameLicense;

namespace WheelWizard.Settings.Views;

/// <summary>One disc per region. The region in use decides which disc starts and which save the game uses.</summary>
public partial class GameDiscSettings : UserControl
{
    private static readonly MarioKartWiiEnums.Regions[] Regions =
    [
        MarioKartWiiEnums.Regions.America,
        MarioKartWiiEnums.Regions.Europe,
        MarioKartWiiEnums.Regions.Japan,
        MarioKartWiiEnums.Regions.Korea,
    ];

    private readonly ISettingsManager _settings;
    private readonly IFilePickerService _filePicker;
    private readonly IGameLicenseSingletonService _licenses;
    private readonly IDiscImageService _discs;

    public GameDiscSettings(
        ISettingsManager settings,
        IFilePickerService filePicker,
        IGameLicenseSingletonService licenses,
        IDiscImageService discs
    )
    {
        _settings = settings;
        _filePicker = filePicker;
        _licenses = licenses;
        _discs = discs;
        InitializeComponent();
        Rebuild();
    }

    private static string DiscRegionName(MarioKartWiiEnums.Regions region) =>
        region switch
        {
            MarioKartWiiEnums.Regions.America => "America",
            MarioKartWiiEnums.Regions.Europe => "Europe",
            MarioKartWiiEnums.Regions.Japan => "Japan",
            _ => "South Korea",
        };

    private FilePickerFileType DiscFile =>
        new("Mario Kart Wii disc")
        {
            Patterns = _discs.SupportedExtensions.Select(extension => "*" + extension).ToList(),
            MimeTypes = ["application/octet-stream"],
        };

    private void Rebuild()
    {
        DiscList.Children.Clear();
        var current = _settings.Get<MarioKartWiiEnums.Regions>(_settings.RR_REGION);
        var stored = _settings.Get<string>(_settings.GAME_DISCS);
        foreach (var region in Regions)
        {
            var path = RegionDiscs.PathFor(stored, region);
            var active = region == current;

            var title = new TextBlock
            {
                Text = DiscRegionName(region) + (active ? "   In use" : string.Empty),
                FontWeight = FontWeight.SemiBold,
            };
            var file = new TextBlock
            {
                Text = path is null ? "No disc added" : System.IO.Path.GetFileName(path),
                FontSize = 12,
                Opacity = 0.7,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(title);
            text.Children.Add(file);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var add = new WheelWizard.Views.Components.Button
            {
                Text = path is null ? "Add" : "Change",
                ButtonSize = WheelWizard.Views.Components.Button.ButtonsSizeType.Compact,
            };
            add.Click += async (_, _) => await AddAsync(region);
            buttons.Children.Add(add);
            if (path is not null && !active)
            {
                var use = new WheelWizard.Views.Components.Button
                {
                    Text = "Use",
                    ButtonSize = WheelWizard.Views.Components.Button.ButtonsSizeType.Compact,
                };
                use.Click += (_, _) => Use(region);
                buttons.Children.Add(use);
            }

            if (path is not null)
            {
                var remove = new WheelWizard.Views.Components.Button
                {
                    Text = "Remove",
                    ButtonSize = WheelWizard.Views.Components.Button.ButtonsSizeType.Compact,
                };
                remove.Click += (_, _) => Remove(region);
                buttons.Children.Add(remove);
            }

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                MinHeight = 44,
                Margin = new Avalonia.Thickness(0, 6),
            };
            row.Children.Add(text);
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);

            var card = new Border { Child = row };
            card.Classes.Add("SettingsBlockStyle");
            card.Classes.Add("SettingsRowStyle");
            DiscList.Children.Add(card);
        }
    }

    private async Task AddAsync(MarioKartWiiEnums.Regions region)
    {
        var path = await _filePicker.OpenSingleFileAsync($"Pick the {DiscRegionName(region)} disc", [DiscFile]);
        if (path is null)
            return;

        // The disc header says which game and region it is, whatever the file format (ISO, WBFS, CISO, GCZ, WIA, RVZ).
        var info = _discs.ReadInfo(path);
        if (info.IsFailure)
        {
            ViewUtils.ShowSnackbar(info.Error.Message, ViewUtils.SnackbarType.Warning);
            return;
        }

        var detected = GameRegion.FromGameId(info.Value.GameId);
        if (detected == MarioKartWiiEnums.Regions.None)
        {
            ViewUtils.ShowSnackbar("That does not look like a Mario Kart Wii disc.", ViewUtils.SnackbarType.Warning);
            return;
        }

        if (detected != region)
        {
            ViewUtils.ShowSnackbar(
                $"That is the {DiscRegionName(detected)} disc. Add it on the {DiscRegionName(detected)} row.",
                ViewUtils.SnackbarType.Warning
            );
            return;
        }

        _settings.Set(_settings.GAME_DISCS, RegionDiscs.WithPath(_settings.Get<string>(_settings.GAME_DISCS), region, path));
        // The disc of the region in use is the one that starts.
        var current = _settings.Get<MarioKartWiiEnums.Regions>(_settings.RR_REGION);
        if (region == current || string.IsNullOrWhiteSpace(_settings.Get<string>(_settings.GAME_LOCATION)))
            _settings.Set(_settings.GAME_LOCATION, path);
        Rebuild();
        ViewUtils.ShowSnackbar($"{DiscRegionName(region)} disc added ({info.Value.Format.ToString().ToUpperInvariant()}).");
    }

    private void Use(MarioKartWiiEnums.Regions region)
    {
        // Choosing the region also switches the game location to its disc.
        _settings.Set(_settings.RR_REGION, region);
        _licenses.LoadLicense();
        ViewUtils.GetLayout().UpdateSidebarProfile();
        ViewUtils.GetLayout().UpdateFriendCount();
        Rebuild();
    }

    private void Remove(MarioKartWiiEnums.Regions region)
    {
        _settings.Set(_settings.GAME_DISCS, RegionDiscs.WithPath(_settings.Get<string>(_settings.GAME_DISCS), region, null));
        Rebuild();
    }
}
