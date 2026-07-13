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
    public void RecaptureUsesTheCompleteCollectionPipeline()
    {
        var plan = CollectionTaskModeCatalog.BuildPlan(
            CollectionTaskModeValues.Recapture,
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
    public void FullModeIsPresentedAsTheDefaultCollectionAction()
    {
        var option = Assert.Single(
            CollectionTaskModeCatalog.Options,
            item => item.Value == CollectionTaskModeValues.Full);

        Assert.Equal("采集", option.Label);
        Assert.Equal("开始采集", option.StartButtonText);
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
}
