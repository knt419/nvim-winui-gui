namespace NvimWinUIGui;

/// <summary>
/// Pure-C# entry point. No XAML in this project (the box's net472 XamlCompiler.exe is broken). The SDK injects a module initializer that bootstraps the Windows App Runtime before Main runs,
/// so Main only has to start the Application.
/// </summary>
public static class Program
{
    [System.STAThread]
    public static void Main()
    {
        Microsoft.UI.Xaml.Application.Start(_ => new App());
    }
}
