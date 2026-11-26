using MatCloud.Contract;
using ThixGate.Core.Business;
using ThixGate.Core.Infrastructure;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ThixGate.Tests;

/// <summary>
/// Every test gets its own data folder, an empty library, a token store in memory and no machine policies. Nothing a test does
/// touches the real data, settings, Keychain or registry.
/// </summary>
internal sealed class TestData : IDisposable
{
    public TestData()
    {
        Folder = Path.Combine(Path.GetTempPath(), "thixgate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        AppPaths.UseDirectoryForTests(Folder);
        SettingsStore.Reset();
        LibraryStore.Reset();
        MatgateStore.Reset();
        TokenStore.UseMemoryForTests(true);
        CloudHandoff.Folder = Path.Combine(Folder, "handoff"); // never the sign-in a real ThixLauncher left
        AccessPolicy.UsePolicyReaderForTests(_ => false);
        CloudClient.HandlerForTests = null;
        MatgateService.HandlerForTests = null;
        MatgateRelay.ConnectForTests = null;
        CloudSyncService.ResetForTests();
    }

    public string Folder { get; }

    public void Dispose()
    {
        CloudSyncService.Stop();
        MatgateRelay.CloseAll();
        MatgateRelay.ConnectForTests = null;
        CloudClient.HandlerForTests = null;
        MatgateService.HandlerForTests = null;
        AccessPolicy.UsePolicyReaderForTests(null);
        TokenStore.UseMemoryForTests(false);
        CloudHandoff.Folder = CloudHandoff.DefaultFolder();
        SettingsStore.Reset();
        LibraryStore.Reset();
        MatgateStore.Reset();
        AppPaths.UseDirectoryForTests(null);
        AppClock.Pin(null);
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
