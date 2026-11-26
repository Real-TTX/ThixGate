using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using MatDesktop;
using ThixGate.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Setup;

/// <summary>
/// The Windows setup of ThixGate in MatDesktop's setup window (SETUP-01, SETUP-02): one window for install, update and
/// uninstall, 600 × 560, the footer with "Version x · channel" (SETUP-07). Install: Welcome (per user, no administrator
/// rights, where to: SETUP-03) → Options → Progress → Finish. Update: Welcome with the installed and the new version →
/// Progress → Finish. Uninstall: what goes and "Auch meine Verbindungen und Zugangsdaten löschen" (off) → Progress → Finish (SETUP-04). The work
/// (InstallService, business layer) runs on a background task and reports its steps; the window only steers and cannot be
/// closed halfway through.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ThixGateSetup
{
    /// <summary>Shows the assistant; <paramref name="finished"/> gets true when the program folder has to be removed after this process ended.</summary>
    public static void Run(bool isUninstall, Action<bool> finished)
    {
        var removeProgramFolder = false;
        var isUpdate = !isUninstall && InstallService.IsInstalled;
        var source = App.ExecutablePath;
        MatSetupWindow? window = null;
        window = Create(isUninstall, isUpdate, InstallService.InstalledVersion, InstallService.ProgramFolder, async run =>
        {
            void Report(string step) => run.Progress.Report(new MatSetupReport(step, MatSetupStepState.Done));
            try
            {
                if (isUninstall)
                {
                    removeProgramFolder = await Task.Run(() => InstallService.Uninstall(run.DeleteData, Report));
                    window!.Steps[^1].Text = UninstalledText(run.DeleteData);
                }
                else
                {
                    var choices = isUpdate ? null : Choices(run.Options);
                    await Task.Run(() => InstallService.Install(source, choices, Report));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                                  or System.Runtime.InteropServices.COMException or System.Security.SecurityException)
            {
                Log.Error(isUninstall ? "Uninstall failed" : "Setup failed", exception);
                throw new InvalidOperationException($"{exception.Message}\n\nDetails stehen im Protokoll ({AppPaths.LogDirectory}).", exception);
            }
        });

        window.Finished += (_, _) =>
        {
            if (window.StartNow)
            {
                InstallService.StartInstalled();
            }

            finished(removeProgramFolder);
        };
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// The window for install (or update) or uninstall, with what it shows decided here once: the mode, the installed
    /// version and the program folder. <paramref name="work"/> is the work of the Install, Update or Uninstall button.
    /// The self test and the snapshots pass work that touches nothing.
    /// </summary>
    public static MatSetupWindow Create(bool isUninstall, bool isUpdate, string? installedVersion, string programFolder, Func<MatSetupRun, Task> work)
    {
        var mode = isUninstall ? MatSetupMode.Uninstall : isUpdate ? MatSetupMode.Update : MatSetupMode.Setup;
        IReadOnlyList<MatSetupStep> steps = mode switch
        {
            MatSetupMode.Uninstall =>
            [
                MatSetupStep.Uninstall("Programm, Startmenü-Eintrag, Desktop-Verknüpfung und Autostart werden entfernt. Verbindungen in der ThixCloud bleiben in der Cloud."),
            ],
            MatSetupMode.Update =>
            [
                MatSetupStep.Welcome(string.Empty, Note("Ein laufendes ThixGate wird beendet und danach wieder gestartet. Offene Sitzungen werden dabei getrennt; Verbindungen und Zugangsdaten bleiben erhalten.")),
            ],
            _ =>
            [
                // SETUP-03: for the user, without administrator rights, and where to. The folder on a line of its own: wrapped
                // inside a sentence the path breaks after "C:".
                MatSetupStep.Welcome(
                    "ThixGate verwaltet deine Remote-Verbindungen an einem Ort – Remote Desktop, VNC und SSH, mit Ordnern und gespeicherten Zugangsdaten. Auf Wunsch gleich auf allen Geräten über die ThixCloud, optional über ein Matgate.",
                    Note($"Wird für dein Benutzerkonto installiert – ohne Administratorrechte – nach\n{programFolder}")),
                MatSetupStep.WithOptions(null, [.. Options()]),
            ],
        };

        var finish = isUninstall
            ? MatSetupStep.Finish(UninstalledText(deleteData: false), offersStart: false)
            : MatSetupStep.Finish($"ThixGate {AppInfo.Version} ist installiert. Öffne es über das Startmenü oder das Symbol im Infobereich.");
        var version = isUninstall ? installedVersion ?? AppInfo.Version : AppInfo.Version;
        var window = new MatSetupWindow(new MatSetup(AppInfo.ProductName, version, AppInfo.Channel, mode, steps, work)
        {
            Logo = Ui.Logo,
            WordMark = WordMark(),
            InstalledVersion = installedVersion ?? "eine ältere Version",
            Finish = finish,
            DeleteDataText = "Auch meine Verbindungen und Zugangsdaten löschen",
            DeleteDataDescription = "Verbindungen, Ordner, Zugangsdaten mit Passwörtern und Einstellungen auf diesem Computer. Ohne Haken bleiben sie für eine spätere Installation erhalten. Was in der ThixCloud liegt, bleibt dort.",
        });
        return window;
    }

    /// <summary>
    /// The switches of the Options step from the install options (installer.json, SETUP-08), each on as its default: the same
    /// questions ThixLauncher asks before it installs quietly.
    /// </summary>
    internal static IEnumerable<MatSetupOption> Options()
    {
        var config = InstallerOptions.Config;
        if (config.Autostart is { } autostart)
        {
            yield return MatSetupOption.Autostart("Im Infobereich, ohne Fenster – damit Zeitpläne laufen") with { IsOn = autostart.Default };
        }

        if (config.DesktopShortcut is { } shortcut)
        {
            yield return MatSetupOption.DesktopShortcut("Ein Startmenü-Eintrag wird immer angelegt") with { IsOn = shortcut.Default };
        }

        if (config.AutomaticUpdates is { } updates)
        {
            yield return MatSetupOption.AutomaticUpdates("Neue Versionen ohne Rückfrage, ThixGate startet danach neu") with { IsOn = updates.Default };
        }

        if (config.CliPath is { } cliPath && OperatingSystem.IsWindows())
        {
            yield return new MatSetupOption(CliOptionKey, "Befehlszeile thixgate-cli zum PATH hinzufügen",
                "Damit „thixgate-cli“ in jeder Eingabeaufforderung und PowerShell bereitsteht", cliPath.Default);
        }

        // ThixGate opens no files: installer.json offers no file types, so there is no switch for them.
    }

    /// <summary>The answers of the Options step as install choices.</summary>
    internal static InstallChoices Choices(IReadOnlyDictionary<string, bool> options) => new(
        options.TryGetValue("shortcut", out var shortcut) ? shortcut : null,
        options.TryGetValue("autostart", out var autostart) ? autostart : null,
        options.TryGetValue("updates", out var updates) ? updates : null,
        CliPath: options.TryGetValue(CliOptionKey, out var cliPath) ? cliPath : null);

    /// <summary>The key of the switch "thixgate-cli on the PATH" in the Options step.</summary>
    internal const string CliOptionKey = "cli";

    /// <summary>The word mark of the title bar ("Thix" and "Gate" in the accent text) for the header of the setup.</summary>
    private static TextBlock WordMark()
    {
        var mark = new TextBlock { FontSize = MatTokens.Type.Fs2xl, FontWeight = Avalonia.Media.FontWeight.SemiBold };
        var accent = new Run("Gate");
        accent.Bind(TextElement.ForegroundProperty, mark.GetResourceObservable("Mat.AccentText"));
        mark.Inlines = [new Run("Thix"), accent];
        return mark;
    }

    /// <summary>A note under the text of a step, in the tint of information.</summary>
    private static InfoBar Note(string text)
    {
        var note = new InfoBar { Margin = new Avalonia.Thickness(0) };
        note.Show(text, InfoSeverity.Info);
        return note;
    }

    private static string UninstalledText(bool deleteData) =>
        deleteData ? "Programm, Verbindungen und Zugangsdaten wurden entfernt." : "Das Programm wurde entfernt. Deine Verbindungen und Zugangsdaten bleiben erhalten.";
}
