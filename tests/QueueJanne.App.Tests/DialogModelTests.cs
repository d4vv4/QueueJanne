using QueueJanne.App.ViewModels;

namespace QueueJanne.App.Tests;

public class DialogModelTests
{
    [Fact]
    public void Properties_keep_numbers_and_booleans_and_quotes_make_text()
    {
        Assert.True(SendMessageViewModel.TryParseProperties("tenant = t-0001\nretry = 3\nratio = 0.5\nurgent = true\ncode = \"007\"\n\n", out var properties, out var problem));

        Assert.Null(problem);
        Assert.Equal("t-0001", properties["tenant"]);
        Assert.Equal(3L, properties["retry"]);
        Assert.Equal(0.5, properties["ratio"]);
        Assert.Equal(true, properties["urgent"]);
        Assert.Equal("007", properties["code"]);
    }

    [Fact]
    public void A_property_line_without_a_value_is_reported()
    {
        Assert.False(SendMessageViewModel.TryParseProperties("tenant = t-1\njust a name", out _, out var problem));

        Assert.Equal("Property line 2 needs the form name = value.", problem);
    }

    [Fact]
    public void Palette_puts_names_that_start_with_the_query_first_and_needs_every_word()
    {
        PaletteItem Item(string title, long deadLetters = 0) => new(title, string.Empty, "Demo " + title, deadLetters, () => { });
        IReadOnlyList<PaletteItem> items = [Item("billing-events/subscriptions/ledger"), Item("ledger-sync"), Item("orders", 3), Item("orders-archive")];

        Assert.Equal(["ledger-sync", "billing-events/subscriptions/ledger"], CommandPalette.Search(items, "ledger").Select(i => i.Title));
        Assert.Equal(["billing-events/subscriptions/ledger"], CommandPalette.Search(items, "billing ledger").Select(i => i.Title));
        Assert.Equal("orders", CommandPalette.Search(items, "").First().Title);
    }
}
