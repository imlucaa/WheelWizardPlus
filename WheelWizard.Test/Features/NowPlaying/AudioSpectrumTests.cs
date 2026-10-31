using WheelWizard.NowPlaying;

namespace WheelWizard.Test.Features.NowPlaying;

public class AudioSpectrumTests
{
    private const int Rate = 48000;
    private const int Bars = 22;

    private static float[] Tone(double hz, double seconds, double amplitude = 0.5)
    {
        var samples = new float[(int)(Rate * seconds)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
        return samples;
    }

    [Fact]
    public void ABassNote_LightsTheLeftBars()
    {
        using var spectrum = new AudioSpectrum(Rate);

        var levels = spectrum.Feed(Tone(50, 0.5), Bars);

        Assert.True(levels[..5].Max() > 0.8, string.Join(", ", levels));
        Assert.True(levels[^8..].Max() < 0.1, string.Join(", ", levels));
    }

    [Fact]
    public void AHighNote_LightsTheRightBars()
    {
        using var spectrum = new AudioSpectrum(Rate);

        var levels = spectrum.Feed(Tone(6000, 0.5), Bars);

        Assert.True(levels[^5..].Max() > 0.8, string.Join(", ", levels));
        Assert.True(levels[..8].Max() < 0.1, string.Join(", ", levels));
    }

    [Fact]
    public void Silence_LeavesTheBarsFlat()
    {
        using var spectrum = new AudioSpectrum(Rate);

        var levels = spectrum.Feed(new float[Rate / 2], Bars);

        Assert.All(levels, level => Assert.True(level < 0.05, string.Join(", ", levels)));
    }
}
