namespace EmsScout.Application.Collection;

public static class CollectionStepExitPolicy
{
    public static bool IsAccepted(string stepKey, int exitCode)
    {
        return exitCode == 0 ||
               (stepKey.Equals("quality", StringComparison.OrdinalIgnoreCase) && exitCode == 2);
    }
}
