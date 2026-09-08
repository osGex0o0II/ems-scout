using Windows.ApplicationModel;

namespace EmsScout.Desktop.Services;

public sealed class StartupTaskService
{
    public const string TaskId = "EMSScoutStartup";

    public async Task<bool> ApplyAsync(bool enabled)
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (enabled)
            {
                if (task.State == StartupTaskState.Enabled)
                {
                    return true;
                }

                var state = await task.RequestEnableAsync();
                return state is StartupTaskState.Enabled;
            }

            if (task.State == StartupTaskState.Disabled)
            {
                return true;
            }

            task.Disable();
            return true;
        }
        catch (Exception)
        {
            // Debug/unpackaged runs do not have a StartupTask identity.
            return false;
        }
    }
}
