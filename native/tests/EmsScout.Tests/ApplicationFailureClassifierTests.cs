using EmsScout.Application.Errors;
using EmsScout.Application.Workflows;
using EmsScout.Infrastructure.Errors;
using EmsScout.Infrastructure.Importing;
using EmsScout.Infrastructure.Migrations;
using EmsScout.Infrastructure.Sidecar;

namespace EmsScout.Tests;

public sealed class ApplicationFailureClassifierTests
{
    [Theory]
    [InlineData(WorkflowTerminalOutcome.Rejected, "collection_quality_rejected", ApplicationErrorCategory.Quality)]
    [InlineData(WorkflowTerminalOutcome.AuthRequired, "ems_authentication_required", ApplicationErrorCategory.Authentication)]
    [InlineData(WorkflowTerminalOutcome.Cancelled, "collection_cancelled", ApplicationErrorCategory.Cancelled)]
    [InlineData(WorkflowTerminalOutcome.InternalError, "sidecar_internal_error", ApplicationErrorCategory.Collection)]
    public void ClassifiesWorkflowTerminalOutcomes(
        WorkflowTerminalOutcome outcome,
        string code,
        ApplicationErrorCategory category)
    {
        var failure = ApplicationFailureClassifier.Classify(
            new WorkflowExecutionException("采集", outcome, 2, "terminal detail"));

        Assert.Equal(code, failure.Code);
        Assert.Equal(category, failure.Category);
        Assert.Contains("terminal detail", failure.TechnicalDetail);
        Assert.DoesNotContain("terminal detail", failure.UserMessage);
    }

    [Fact]
    public void ClassifiesSnapshotAndWorkflowProtocolFailuresAsContractErrors()
    {
        var snapshot = ApplicationFailureClassifier.Classify(
            new CollectionSnapshotContractException("bad snapshot"));
        var workflow = ApplicationFailureClassifier.Classify(
            new WorkflowEventParseException("bad event"));

        Assert.Equal("snapshot_contract_invalid", snapshot.Code);
        Assert.Equal(ApplicationErrorCategory.Contract, snapshot.Category);
        Assert.Equal("protocol_data_invalid", workflow.Code);
        Assert.Equal(ApplicationErrorCategory.Contract, workflow.Category);
    }

    [Fact]
    public void ClassifiesMigrationEnvironmentAndUnknownFailures()
    {
        var migration = ApplicationFailureClassifier.Classify(new SchemaMigrationException("migration"));
        var missing = ApplicationFailureClassifier.Classify(new FileNotFoundException("missing"));
        var unknown = ApplicationFailureClassifier.Classify(new InvalidOperationException("secret detail"));

        Assert.Equal(ApplicationErrorCategory.Database, migration.Category);
        Assert.Equal("required_file_missing", missing.Code);
        Assert.Equal("internal_unexpected_error", unknown.Code);
        Assert.DoesNotContain("secret detail", unknown.DisplayText);
        Assert.Contains("secret detail", unknown.TechnicalDetail);
    }
}
