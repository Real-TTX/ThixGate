using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

/// <summary>
/// Reading and changing the settings of this computer. Every change is checked here (values, policy) before it is
/// saved; the UI only offers. <see cref="Changed"/> tells the app (theme, tray) what is new.
/// </summary>
public static class SettingsService
{
    /// <summary>Raised after every saved change (any thread).</summary>
    public static event EventHandler<AppSettings>? Changed;

    public static AppSettings Get() => SettingsStore.Read();

    /// <summary>
    /// Changes general settings (not the cloud connection: <see cref="CloudSyncService"/>; not the Matgate accounts:
    /// <see cref="MatgateService"/>).
    /// </summary>
    public static AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var current = SettingsStore.Read();
        var next = change(current) with { Cloud = current.Cloud, MatgateAccounts = current.MatgateAccounts };
        Validate(next);
        return Save(_ => next);
    }

    /// <summary>Things the app remembers by itself (window size, folded menu, first start): no configuration lock.</summary>
    public static AppSettings Remember(Func<AppSettings, AppSettings> change)
    {
        var current = SettingsStore.Read();
        var next = change(current);
        return Save(_ => current with
        {
            WindowWidth = Math.Clamp(next.WindowWidth, 720, 4000),
            WindowHeight = Math.Clamp(next.WindowHeight, 480, 3000),
            IsSidebarCollapsed = next.IsSidebarCollapsed,
            FirstStartDone = next.FirstStartDone,
        });
    }

    /// <summary>The cloud part (only <see cref="CloudSyncService"/>).</summary>
    internal static AppSettings UpdateCloud(Func<CloudSettings, CloudSettings> change) =>
        Save(settings => settings with { Cloud = change(settings.Cloud) });

    /// <summary>The Matgate accounts (only <see cref="MatgateService"/>, after its checks).</summary>
    internal static AppSettings UpdateMatgateAccounts(Func<IReadOnlyList<MatgateAccount>, IReadOnlyList<MatgateAccount>> change) =>
        Save(settings => settings with { MatgateAccounts = change(settings.MatgateAccounts) });

    private static AppSettings Save(Func<AppSettings, AppSettings> change)
    {
        var saved = SettingsStore.Update(change);
        Changed?.Invoke(null, saved);
        return saved;
    }

    private static void Validate(AppSettings settings)
    {
        if (!Enum.IsDefined(settings.Theme) || !Enum.IsDefined(settings.OpenSessionIn))
        {
            throw new BusinessException("Diese Auswahl gibt es nicht.");
        }

        if (settings.SshTerminal.Length > 500)
        {
            throw new BusinessException("Der Befehl für das Terminal ist zu lang.", nameof(AppSettings.SshTerminal));
        }
    }
}
