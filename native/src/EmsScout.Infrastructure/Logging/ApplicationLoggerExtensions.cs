using EmsScout.Application.Errors;
using EmsScout.Application.Logging;
using EmsScout.Infrastructure.Errors;

namespace EmsScout.Infrastructure.Logging;

public static class ApplicationLoggerExtensions
{
    public static ApplicationFailure WriteFailure(
        this IApplicationLogger logger,
        Exception exception,
        string category,
        string eventName = "operation_failed",
        string? stage = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var failure = ApplicationFailureClassifier.Classify(exception);
        logger.Write(new ApplicationLogEvent(
            ApplicationLogLevel.Error,
            category,
            eventName,
            failure.Title,
            new ApplicationLogContext(
                Stage: stage,
                ErrorCode: failure.Code,
                Retryable: failure.IsRetryable),
            exception,
            new Dictionary<string, object?>
            {
                ["suggestedAction"] = failure.SuggestedAction,
            }));
        return failure;
    }
}
