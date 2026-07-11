namespace EmsScout.Application.Errors;

public enum ApplicationErrorCategory
{
    Configuration,
    Environment,
    Authentication,
    Contract,
    Database,
    Quality,
    Collection,
    Cancelled,
    Internal,
}

public sealed record ApplicationFailure(
    string Code,
    ApplicationErrorCategory Category,
    string Title,
    string UserMessage,
    string SuggestedAction,
    bool IsRetryable,
    string TechnicalDetail)
{
    public string DisplayText => $"{Title}（错误代码 {Code}）：{UserMessage}";
}
