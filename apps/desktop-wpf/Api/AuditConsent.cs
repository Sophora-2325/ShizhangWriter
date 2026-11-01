namespace NovelGm.Desktop.Api;

internal static class AuditConsent
{
    internal static async Task ExecuteAsync(Func<bool> confirm, Func<Task> execute)
    {
        if (confirm()) await execute();
    }
}
