namespace EmsScout.Application.Devices;

public sealed record DeviceHealthAssessment(
    string Status,
    string Label,
    string IssueSummary,
    bool NeedsReview,
    bool HasTemperatureIssue);
