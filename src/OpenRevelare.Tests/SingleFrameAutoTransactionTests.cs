using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public class SingleFrameAutoTransactionTests
{
    [Fact]
    public void Base_failure_rolls_back_without_measuring_highlight()
    {
        int highlights = 0, rollbacks = 0;

        bool committed = SingleFrameAutoTransaction.Run(
            () => false,
            () => { highlights++; return true; },
            () => rollbacks++);

        Assert.False(committed);
        Assert.Equal(0, highlights);
        Assert.Equal(1, rollbacks);
    }

    [Fact]
    public void Highlight_failure_rolls_back_once()
    {
        int rollbacks = 0;

        bool committed = SingleFrameAutoTransaction.Run(
            () => true,
            () => false,
            () => rollbacks++);

        Assert.False(committed);
        Assert.Equal(1, rollbacks);
    }

    [Fact]
    public void Successful_measurements_commit_without_rollback()
    {
        int rollbacks = 0;

        bool committed = SingleFrameAutoTransaction.Run(
            () => true,
            () => true,
            () => rollbacks++);

        Assert.True(committed);
        Assert.Equal(0, rollbacks);
    }

    [Fact]
    public void Exception_rolls_back_before_it_escapes()
    {
        int rollbacks = 0;

        Assert.Throws<InvalidOperationException>(() => SingleFrameAutoTransaction.Run(
            () => true,
            () => throw new InvalidOperationException("failed"),
            () => rollbacks++));

        Assert.Equal(1, rollbacks);
    }
}
