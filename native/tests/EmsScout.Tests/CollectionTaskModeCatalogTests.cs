using EmsScout.Application.Collection;

namespace EmsScout.Tests;

public sealed class CollectionTaskModeCatalogTests
{
    [Fact]
    public void FullModeRunsCompleteVerifiedPipeline()
    {
        var plan = CollectionTaskModeCatalog.BuildPlan(
            CollectionTaskModeValues.Full,
            new CollectionCustomTaskOptions(false, false, false, false));

        Assert.True(plan.RequiresBuildings);
        Assert.True(plan.RunEnumeration);
        Assert.True(plan.RunValidation);
        Assert.True(plan.RunImport);
        Assert.True(plan.RunQuality);
        Assert.True(plan.RunRealtimeDetails);
        Assert.True(plan.RunRealtimeAudit);
    }

    [Fact]
    public void ValidateOnlyDoesNotRequireBuildingSelectionOrModifySqlite()
    {
        var plan = CollectionTaskModeCatalog.BuildPlan(
            CollectionTaskModeValues.ValidateOnly,
            new CollectionCustomTaskOptions(true, true, true, true));

        Assert.False(plan.RequiresBuildings);
        Assert.False(plan.RunEnumeration);
        Assert.True(plan.RunValidation);
        Assert.False(plan.RunImport);
        Assert.False(plan.RunQuality);
        Assert.False(plan.RunRealtimeDetails);
        Assert.False(plan.RunRealtimeAudit);
    }

    [Fact]
    public void ImportOnlyRunsOnlyImportStep()
    {
        var plan = CollectionTaskModeCatalog.BuildPlan(
            CollectionTaskModeValues.ImportOnly,
            new CollectionCustomTaskOptions(false, false, false, false));

        Assert.True(plan.RequiresBuildings);
        Assert.False(plan.RunEnumeration);
        Assert.False(plan.RunValidation);
        Assert.True(plan.RunImport);
        Assert.False(plan.RunQuality);
        Assert.False(plan.RunRealtimeDetails);
        Assert.False(plan.RunRealtimeAudit);
    }

    [Fact]
    public void RealtimeDetailsModeRunsDetailsAndAuditWithoutEnumeration()
    {
        var plan = CollectionTaskModeCatalog.BuildPlan(
            CollectionTaskModeValues.RealtimeDetailsOnly,
            new CollectionCustomTaskOptions(false, false, false, false));

        Assert.True(plan.RequiresBuildings);
        Assert.False(plan.RunEnumeration);
        Assert.False(plan.RunValidation);
        Assert.False(plan.RunImport);
        Assert.False(plan.RunQuality);
        Assert.True(plan.RunRealtimeDetails);
        Assert.True(plan.RunRealtimeAudit);
    }

    [Fact]
    public void CustomModeUsesExplicitToggleCombination()
    {
        var plan = CollectionTaskModeCatalog.BuildPlan(
            CollectionTaskModeValues.Custom,
            new CollectionCustomTaskOptions(
                RunImportAfterCollect: true,
                RunQualityAfterImport: false,
                RunRealtimeDetailsAfterImport: true,
                RunRealtimeAuditAfterDetails: false));

        Assert.True(plan.RequiresBuildings);
        Assert.True(plan.RunEnumeration);
        Assert.True(plan.RunValidation);
        Assert.True(plan.RunImport);
        Assert.False(plan.RunQuality);
        Assert.True(plan.RunRealtimeDetails);
        Assert.False(plan.RunRealtimeAudit);
    }

    [Theory]
    [InlineData("quality", 0, true)]
    [InlineData("quality", 2, true)]
    [InlineData("quality", 1, false)]
    [InlineData("default", 2, false)]
    public void QualityExitCodeTwoMeansReviewRequiredInsteadOfTaskFailure(
        string stepKey,
        int exitCode,
        bool expected)
    {
        Assert.Equal(expected, CollectionStepExitPolicy.IsAccepted(stepKey, exitCode));
    }
}
