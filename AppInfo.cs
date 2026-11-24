using System.Reflection;

namespace LabelPrinter;

internal static class AppInfo
{
    public const string Name = "LabelPrinter";

    /// <summary>Public GitHub repo with both the source and the Velopack releases.</summary>
    public const string RepoUrl = "https://github.com/sergiocampama/labelprinter";

    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>Loads the app icon at the closest available size.</summary>
    public static Icon LoadIcon(Size size)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "labelprinter.ico");
            if (File.Exists(path))
                return new Icon(path, size);
        }
        catch { /* fall back below */ }

        return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
    }
}
