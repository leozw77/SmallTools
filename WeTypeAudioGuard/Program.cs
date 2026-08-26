namespace WeTypeAudioGuard;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(
            initiallyOwned: true,
            name: @"Local\WeTypeAudioGuard.Singleton",
            createdNew: out bool createdNew);

        if (!createdNew) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
    }
}
