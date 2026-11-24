using Velopack;
using Velopack.Sources;

namespace LabelPrinter;

/// <summary>
/// Checks the GitHub releases of the public repo for a newer version and, if
/// there is one, downloads it and restarts into it (back in the tray).
/// </summary>
internal static class UpdateService
{
    private static int _running;

    /// <param name="userInitiated">Report every outcome (tray menu item); background checks stay quiet.</param>
    /// <param name="canRestart">When false, an update is downloaded but applied on a later check.</param>
    public static async Task CheckAsync(bool userInitiated, bool canRestart, Action<string> notify)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return;
        try
        {
            var mgr = new UpdateManager(new GithubSource(AppInfo.RepoUrl, accessToken: null, prerelease: false));

            // Running from source (dotnet run / F5): nothing to update.
            if (!mgr.IsInstalled)
            {
                if (userInitiated)
                    notify("Las actualizaciones solo funcionan en la versión instalada.");
                return;
            }

            var update = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                if (userInitiated)
                    notify($"Ya tienes la última versión (v{mgr.CurrentVersion}).");
                return;
            }

            var version = update.TargetFullRelease.Version;
            if (userInitiated)
                notify($"Descargando la versión {version}...");
            await mgr.DownloadUpdatesAsync(update).ConfigureAwait(false);

            if (!canRestart)
                return;

            mgr.ApplyUpdatesAndRestart(update.TargetFullRelease, [Program.BackgroundArg]);
        }
        catch (Exception ex)
        {
            // Update problems must never take the app down.
            if (userInitiated)
                notify($"No se pudo buscar actualizaciones: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
