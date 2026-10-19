using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Resources;

namespace NoteEvolution.UI.Tests;

public class BlockDateTextTests : UiTestContext
{
    private static readonly DateTime Created = new(2026, 10, 12, 9, 15, 0);

    private static readonly DateTime Updated = new(2026, 10, 19, 14, 32, 0);

    private IStringLocalizer<Strings> L => Services.GetRequiredService<IStringLocalizer<Strings>>();

    [Fact]
    public void Of_Both_CreatedWithoutTime()
    {
        Assert.Equal("erstellt 12.10.2026 · geändert 19.10.2026 14:32", BlockDateText.Of(Created, Updated, L));
    }

    [Fact]
    public void Of_SameDay_CreatedWithTime()
    {
        var created = new DateTime(2026, 10, 19, 9, 15, 0);

        Assert.Equal("erstellt 19.10.2026 09:15 · geändert 19.10.2026 14:32", BlockDateText.Of(created, Updated, L));
    }

    [Fact]
    public void Of_OnlyUpdated_OnlyCreated_None()
    {
        Assert.Equal("geändert 19.10.2026 14:32", BlockDateText.Of(null, Updated, L));
        Assert.Equal("erstellt 12.10.2026", BlockDateText.Of(Created, null, L));
        Assert.Null(BlockDateText.Of(null, null, L));
    }

    [Fact]
    public void TitleOf_BothInFull_MissingPartDropped()
    {
        Assert.Equal("Erstellt: 12.10.2026 09:15\nGeändert: 19.10.2026 14:32", BlockDateText.TitleOf(Created, Updated, L));
        Assert.Equal("Geändert: 19.10.2026 14:32", BlockDateText.TitleOf(null, Updated, L));
        Assert.Equal("Erstellt: 12.10.2026 09:15", BlockDateText.TitleOf(Created, null, L));
        Assert.Null(BlockDateText.TitleOf(null, null, L));
    }

    [Fact]
    public void LabelOf_TextAndTitle_NullWithoutDates()
    {
        Assert.Equal(
            new BlockDateLabel("erstellt 12.10.2026 · geändert 19.10.2026 14:32", "Erstellt: 12.10.2026 09:15\nGeändert: 19.10.2026 14:32"),
            BlockDateText.LabelOf(Created, Updated, L));
        Assert.Null(BlockDateText.LabelOf(null, null, L));
    }
}
