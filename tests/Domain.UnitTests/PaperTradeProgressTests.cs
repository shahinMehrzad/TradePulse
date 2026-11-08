namespace Domain.UnitTests;

/// <summary>How far a paper trade has been followed.</summary>
public class PaperTradeProgressTests
{
    [Fact]
    public void ATradeIsCheckedFromItsEntry() =>
        Assert.Equal(PaperTestData.Now, PaperTestData.Opened(PaperTestData.Long()).LastCheckedAt);

    [Fact]
    public void MarkCheckedMovesForward()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        trade.MarkChecked(PaperTestData.Now.AddMinutes(5));

        Assert.Equal(PaperTestData.Now.AddMinutes(5), trade.LastCheckedAt);
    }

    [Fact]
    public void MarkCheckedNeverMovesBack()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());
        trade.MarkChecked(PaperTestData.Now.AddMinutes(5));

        trade.MarkChecked(PaperTestData.Now.AddMinutes(2));

        Assert.Equal(PaperTestData.Now.AddMinutes(5), trade.LastCheckedAt);
    }

    [Fact]
    public void MarkCheckedIsIgnoredOnceTheTradeHasEnded()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());
        trade.MarkChecked(PaperTestData.Now.AddMinutes(1));
        trade.Observe(104m, PaperTestData.Now.AddMinutes(2));

        trade.MarkChecked(PaperTestData.Now.AddMinutes(9));

        Assert.Equal(PaperTestData.Now.AddMinutes(1), trade.LastCheckedAt);
    }
}
