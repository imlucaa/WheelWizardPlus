using System.Numerics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;

namespace WheelWizard.Views.Shell.Startup;

public partial class SplashWindow : Window
{
    private CompositionVisual? _wheelVisual;
    private CompositionAnimationGroup? _motion;
    private readonly bool _lucky = LuckyBoot.Roll();
    private readonly System.Diagnostics.Stopwatch _shown = new();

    /// <summary>True on the 1 in 100 start (the gold token).</summary>
    public bool IsLucky => _lucky;

    /// <summary>A lucky start keeps the token on screen for 3 seconds, so it is seen even when the app is quick.</summary>
    public Task HoldAsync(CancellationToken cancellationToken) =>
        _lucky && _shown.Elapsed < TimeSpan.FromSeconds(3)
            ? Task.Delay(TimeSpan.FromSeconds(3) - _shown.Elapsed, cancellationToken)
            : Task.CompletedTask;

    private static readonly Color Gold = Color.Parse("#FFD54A");

    public SplashWindow()
    {
        AvaloniaXamlLoader.Load(this);
        // Linux's managed decoration border surrounds even this undecorated transparent window.
        if (OperatingSystem.IsLinux())
            WindowDecorationsTheme = null;
        if (_lucky)
            ShowToken();
    }

    // The gold token instead of the wheel, with a gold glow; the sparkles go on around it once the window is open.
    private void ShowToken()
    {
        var wheel = this.FindControl<Image>("Wheel")!;
        wheel.Source = new Bitmap(AssetLoader.Open(new Uri("avares://BetterWheelWizard/Resources/Images/LuckyToken.png")));
        wheel.Margin = new Thickness(24);
        wheel.Effect = new DropShadowEffect
        {
            Color = Gold,
            BlurRadius = 30,
            OffsetX = 0,
            OffsetY = 0,
            Opacity = 1,
        };
        // A bigger (still see-through) window, so the token can jump out at you.
        Width = Height = 900;
        // A jumpscare has to be seen: above every other window, even though the splash never takes the focus.
        Topmost = true;
        var sparkles = this.FindControl<Canvas>("Sparkles")!;
        sparkles.IsVisible = true;
        // Twelve four-point stars on a ring just outside the token, alternating big and small.
        for (var i = 0; i < 12; i++)
        {
            var angle = i * Math.Tau / 12;
            var size = i % 2 == 0 ? 18.0 : 11.0;
            var radius = 92 + (i % 3) * 6;
            var star = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M 0.5,0 L 0.62,0.38 L 1,0.5 L 0.62,0.62 L 0.5,1 L 0.38,0.62 L 0,0.5 L 0.38,0.38 Z"),
                Fill = new SolidColorBrush(i % 3 == 0 ? Colors.White : Gold),
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size,
                Effect = new DropShadowEffect
                {
                    Color = Gold,
                    BlurRadius = 8,
                    OffsetX = 0,
                    OffsetY = 0,
                },
                Opacity = 0,
            };
            Canvas.SetLeft(star, 110 + Math.Cos(angle) * radius - size / 2);
            Canvas.SetTop(star, 110 + Math.Sin(angle) * radius - size / 2);
            sparkles.Children.Add(star);
        }
    }

    // The jumpscare: token and sparkles slam in from small to nearly four times the size, shaking, then settle big.
    private void JumpOut()
    {
        var entrance = this.FindControl<Grid>("Entrance")!;
        if (ElementComposition.GetElementVisual(entrance) is not { } visual)
            return;
        visual.CenterPoint = new Vector3D(entrance.Bounds.Width / 2, entrance.Bounds.Height / 2, 0);
        var compositor = visual.Compositor;
        var slam = compositor.CreateVector3DKeyFrameAnimation();
        slam.Target = "Scale";
        slam.Duration = TimeSpan.FromMilliseconds(700);
        slam.InsertKeyFrame(0, new Vector3D(0.15, 0.15, 1));
        slam.InsertKeyFrame(0.18f, new Vector3D(3.9, 3.9, 1));
        slam.InsertKeyFrame(0.35f, new Vector3D(3.3, 3.3, 1));
        slam.InsertKeyFrame(0.55f, new Vector3D(3.7, 3.7, 1));
        slam.InsertKeyFrame(1, new Vector3D(3.5, 3.5, 1));
        var shake = compositor.CreateQuaternionKeyFrameAnimation();
        shake.Target = "Orientation";
        shake.Duration = TimeSpan.FromMilliseconds(600);
        var angles = new[] { 0f, -9f, 8f, -7f, 6f, -4f, 3f, -1.5f, 0f };
        for (var i = 0; i < angles.Length; i++)
            shake.InsertKeyFrame(i / (float)(angles.Length - 1), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angles[i] * MathF.PI / 180));
        var flash = compositor.CreateScalarKeyFrameAnimation();
        flash.Target = "Opacity";
        flash.Duration = TimeSpan.FromMilliseconds(120);
        flash.InsertKeyFrame(0, 0);
        flash.InsertKeyFrame(1, 1);
        _motion = compositor.CreateAnimationGroup();
        _motion.Add(slam);
        _motion.Add(shake);
        _motion.Add(flash);
        _wheelVisual = visual;
        visual.StartAnimationGroup(_motion);
    }

    // Each sparkle fades and grows in and out on its own beat, so the outline twinkles.
    private void TwinkleSparkles()
    {
        var sparkles = this.FindControl<Canvas>("Sparkles")!;
        for (var i = 0; i < sparkles.Children.Count; i++)
        {
            var star = sparkles.Children[i];
            if (ElementComposition.GetElementVisual(star) is not { } visual)
                continue;
            visual.CenterPoint = new Vector3D(star.Bounds.Width / 2, star.Bounds.Height / 2, 0);
            var compositor = visual.Compositor;
            var delay = TimeSpan.FromMilliseconds(i * 83);
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Target = "Opacity";
            fade.Duration = TimeSpan.FromMilliseconds(1000);
            fade.DelayTime = delay;
            fade.IterationBehavior = AnimationIterationBehavior.Forever;
            fade.InsertKeyFrame(0, 0);
            fade.InsertKeyFrame(0.5f, 1);
            fade.InsertKeyFrame(1, 0);
            var grow = compositor.CreateVector3DKeyFrameAnimation();
            grow.Target = "Scale";
            grow.Duration = TimeSpan.FromMilliseconds(1000);
            grow.DelayTime = delay;
            grow.IterationBehavior = AnimationIterationBehavior.Forever;
            grow.InsertKeyFrame(0, new Vector3D(0.3, 0.3, 1));
            grow.InsertKeyFrame(0.5f, new Vector3D(1.2, 1.2, 1));
            grow.InsertKeyFrame(1, new Vector3D(0.3, 0.3, 1));
            var group = compositor.CreateAnimationGroup();
            group.Add(fade);
            group.Add(grow);
            visual.StartAnimationGroup(group);
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _shown.Start();
        if (_motion is not null)
            return;
        UpdateLayout();
        if (_lucky)
        {
            TwinkleSparkles();
            LuckyBoot.PlaySound();
            JumpOut();
            return;
        }
        var wheel = this.FindControl<Image>("Wheel")!;
        _wheelVisual = ElementComposition.GetElementVisual(wheel);
        if (_wheelVisual is null)
            return;
        _wheelVisual.CenterPoint = new Vector3D(wheel.Bounds.Width / 2, wheel.Bounds.Height / 2, 0);
        var compositor = _wheelVisual.Compositor;
        var entranceDuration = TimeSpan.FromMilliseconds(180);
        var ease = new CubicEaseOut();
        var scale = compositor.CreateVector3DKeyFrameAnimation();
        scale.Target = "Scale";
        scale.Duration = entranceDuration;
        scale.InsertKeyFrame(0, new Vector3D(0.85, 0.85, 1));
        scale.InsertKeyFrame(1, new Vector3D(1, 1, 1), ease);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.Duration = entranceDuration;
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        var spin = compositor.CreateQuaternionKeyFrameAnimation();
        spin.Target = "Orientation";
        spin.Duration = TimeSpan.FromSeconds(1.5);
        spin.IterationBehavior = AnimationIterationBehavior.Forever;
        spin.InsertKeyFrame(0, Quaternion.Identity);
        spin.InsertKeyFrame(0.25f, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2));
        spin.InsertKeyFrame(0.5f, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI));
        spin.InsertKeyFrame(0.75f, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 1.5f));
        spin.InsertKeyFrame(1, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Tau));
        _motion = compositor.CreateAnimationGroup();
        _motion.Add(scale);
        _motion.Add(fade);
        _motion.Add(spin);
        // One render-thread start: UI initialization cannot pause or restart this motion.
        _wheelVisual.StartAnimationGroup(_motion);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_motion is not null)
            _wheelVisual?.StopAnimationGroup(_motion);
        base.OnClosed(e);
    }
}
