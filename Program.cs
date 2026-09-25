namespace PhrasePaste;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Pin CWD to the app folder so any relative asset resolves regardless of launch method.
        Environment.CurrentDirectory = AppContext.BaseDirectory;
        ApplicationConfiguration.Initialize();

        if (args.Length > 0 && args[0] == "--selftest")
        {
            var outputPath = args.Length > 1
                ? args[1]
                : Path.Combine(Path.GetTempPath(), "phrasepaste-selftest.txt");

            var report = SelfTest.Run();
            File.WriteAllText(outputPath, report);

            try
            {
                Console.Write(report);
            }
            catch
            {
                // a GUI-subsystem exe has no console; the file above is the record
            }

            return;
        }

        // Single instance: only one process may own the global hotkeys.
        using var singleInstance = new Mutex(true, @"Local\PhrasePaste", out var createdNew);
        if (!createdNew)
        {
            return; // another instance is already running
        }

        Application.Run(new PhrasePasteApp());
    }
}
