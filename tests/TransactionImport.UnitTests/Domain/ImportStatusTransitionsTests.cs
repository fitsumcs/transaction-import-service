using TransactionImport.Domain.Imports;

namespace TransactionImport.UnitTests.Domain;

public sealed class ImportStatusTransitionsTests
{
    [Theory]
    [InlineData(ImportStatus.Pending, ImportStatus.Processing)]
    [InlineData(ImportStatus.Pending, ImportStatus.Cancelled)]
    [InlineData(ImportStatus.Pending, ImportStatus.Failed)]
    [InlineData(ImportStatus.Processing, ImportStatus.Completed)]
    [InlineData(ImportStatus.Processing, ImportStatus.Failed)]
    [InlineData(ImportStatus.Processing, ImportStatus.Cancelling)]
    [InlineData(ImportStatus.Processing, ImportStatus.Pending)]
    [InlineData(ImportStatus.Cancelling, ImportStatus.Cancelled)]
    [InlineData(ImportStatus.Cancelling, ImportStatus.Failed)]
    public void Allows_legal_transitions(ImportStatus from, ImportStatus to) =>
        Assert.True(ImportStatusTransitions.CanTransition(from, to));

    [Theory]
    [InlineData(ImportStatus.Pending, ImportStatus.Completed)]
    [InlineData(ImportStatus.Pending, ImportStatus.Cancelling)]
    [InlineData(ImportStatus.Processing, ImportStatus.Cancelled)]
    [InlineData(ImportStatus.Cancelling, ImportStatus.Processing)]
    [InlineData(ImportStatus.Cancelling, ImportStatus.Completed)]
    public void Rejects_illegal_transitions(ImportStatus from, ImportStatus to) =>
        Assert.False(ImportStatusTransitions.CanTransition(from, to));

    [Theory]
    [InlineData(ImportStatus.Completed)]
    [InlineData(ImportStatus.Failed)]
    [InlineData(ImportStatus.Cancelled)]
    public void Terminal_statuses_have_no_outgoing_transitions(ImportStatus terminal)
    {
        Assert.True(ImportStatusTransitions.IsTerminal(terminal));
        Assert.All(Enum.GetValues<ImportStatus>(), target => Assert.False(ImportStatusTransitions.CanTransition(terminal, target)));
    }

    [Fact]
    public void AllowedSources_lists_every_status_that_can_reach_the_target() =>
        Assert.Equivalent(
            new[] { ImportStatus.Pending, ImportStatus.Cancelling },
            ImportStatusTransitions.AllowedSources(ImportStatus.Cancelled));
}
