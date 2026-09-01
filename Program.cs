namespace PhrasePaste;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Pin CWD to the app folder so any relative asset resolves regardless of launch method.
        Environment.CurrentDirectory = AppContext.BaseDirectory;
        ApplicationConfiguration.Initialize();

        // Single instance: only one process may own the global hotkeys.
        using var singleInstance = new Mutex(true, @"Local\PhrasePaste", out var createdNew);
        if (!createdNew)
        {
            return; // another instance is already running
        }

        Application.Run(new PhrasePasteApp());
    }
}
