using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The time format of remaining and expected durations (spec 11.1).</summary>
public class DurationsTests : UiTestContext
{
    [Fact]
    public void Durations_Formats_42s_1m20_1h10m()
    {
        var l = Services.GetRequiredService<IStringLocalizer<Strings>>();

        Assert.Equal("42 s", Durations.Format(TimeSpan.FromSeconds(42), l));
        Assert.Equal("1:20", Durations.Format(TimeSpan.FromSeconds(80), l));
        Assert.Equal("1 h 10 min", Durations.Format(TimeSpan.FromMinutes(70), l));

        // The edges: whole seconds (rounded), minutes with two-digit seconds, whole minutes from one hour.
        Assert.Equal("0 s", Durations.Format(TimeSpan.Zero, l));
        Assert.Equal("0 s", Durations.Format(TimeSpan.FromSeconds(-5), l));
        Assert.Equal("59 s", Durations.Format(TimeSpan.FromSeconds(59.4), l));
        Assert.Equal("1:00", Durations.Format(TimeSpan.FromSeconds(59.6), l));
        Assert.Equal("1:05", Durations.Format(TimeSpan.FromSeconds(65), l));
        Assert.Equal("59:59", Durations.Format(TimeSpan.FromSeconds(3599), l));
        Assert.Equal("1 h 0 min", Durations.Format(TimeSpan.FromHours(1), l));
        Assert.Equal("1 h 10 min", Durations.Format(TimeSpan.FromSeconds(70 * 60 + 59), l));
        Assert.Equal("25 h 3 min", Durations.Format(TimeSpan.FromMinutes(25 * 60 + 3), l));
    }
}
