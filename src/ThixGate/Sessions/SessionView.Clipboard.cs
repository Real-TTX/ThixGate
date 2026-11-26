using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Matgate.Display;
using ThixGate.Core.Infrastructure;

namespace ThixGate.Sessions;

/// <summary>
/// The clipboard between this computer and a remote desktop, both ways, as Remote Desktop's own client has it: text, pictures and files
/// (RDP; VNC only text). What is copied on the remote desktop lands on this computer's clipboard at once - files are fetched into a
/// folder of ThixGate's first. What is copied here goes to the session when it gets the focus again (and only when it changed), so Ctrl+V
/// there pastes it. Files go by their path: nothing is copied before the remote side reads it.
/// </summary>
public sealed partial class SessionView
{
    /// <summary>Files from the remote clipboard are fetched up to this much; more would hold the clipboard for too long.</summary>
    private const long MaxFetch = 2L * 1024 * 1024 * 1024;

    // What both sides have, as a fingerprint: set whenever one side's clipboard reached the other, so nothing goes back and forth.
    private string _clipFingerprint = "";
    private int _clipSyncing;
    private CancellationTokenSource? _fetching;

    /// <summary>What the clipboard did last (tests, the bar).</summary>
    internal string ClipboardNote { get; private set; } = "";

    /// <summary>This computer's clipboard to the session, when it changed since both sides last agreed.</summary>
    internal async Task SyncClipboardAsync()
    {
        if (!_isConnected || !IsScreen || _backend is not { } backend || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard
            || Interlocked.Exchange(ref _clipSyncing, 1) == 1)
        {
            return;
        }

        try
        {
            if (backend.ClipFiles && backend is IScreenLocalClipboard local
                && await clipboard.TryGetFilesAsync() is { Length: > 0 } items)
            {
                var paths = items.Select(item => item.TryGetLocalPath()).OfType<string>().ToList();
                var fingerprint = "files:" + string.Join("|", paths);
                if (paths.Count > 0 && fingerprint != _clipFingerprint)
                {
                    _clipFingerprint = fingerprint;
                    local.ClipLocalFiles(paths, false);
                    ClipboardNote = paths.Count == 1 ? "1 Datei in der Zwischenablage der Sitzung" : $"{paths.Count} Dateien in der Zwischenablage der Sitzung";
                }

                return;
            }

            if (backend.ClipImages && await clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                using (bitmap)
                {
                    var (dib, fingerprint) = Dib(bitmap);
                    if (fingerprint != _clipFingerprint)
                    {
                        _clipFingerprint = fingerprint;
                        var id = Guid.NewGuid().ToString("N");
                        backend.Clip("screen.clip.begin", new JsonObject { ["id"] = id, ["kind"] = "image", ["files"] = new JsonArray(new JsonObject { ["name"] = "bild.bmp" }) }, default);
                        backend.Clip("screen.clip.chunk", EngineJson(new JsonObject { ["id"] = id, ["index"] = 0, ["offset"] = 0 }), dib);
                        backend.Clip("screen.clip.end", new JsonObject { ["id"] = id }, default);
                        ClipboardNote = "Bild in der Zwischenablage der Sitzung";
                    }
                }

                return;
            }

            if (await clipboard.TryGetTextAsync() is { Length: > 0 } text)
            {
                var fingerprint = "text:" + text;
                if (fingerprint != _clipFingerprint)
                {
                    _clipFingerprint = fingerprint;
                    backend.Clip("screen.clip.set", new JsonObject { ["paste"] = false }, Encoding.UTF8.GetBytes(text));
                    ClipboardNote = "Text in der Zwischenablage der Sitzung";
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Warning($"Session {_connection.Id}: clipboard to the session: {exception.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _clipSyncing, 0);
        }
    }

    /// <summary>Something was copied on the remote desktop (any thread): text and pictures go to this computer's clipboard, files are fetched first.</summary>
    private void RemoteClipboard(JsonObject data, ReadOnlyMemory<byte> payload)
    {
        var kind = data["kind"]?.GetValue<string>() ?? "";
        switch (kind)
        {
            case "text":
                var text = Encoding.UTF8.GetString(payload.Span);
                Ui.Post(() => _ = SetLocalAsync(clipboard => clipboard.SetTextAsync(text), "text:" + text, "Text aus der Sitzung in der Zwischenablage"));
                break;
            case "image":
                var bmp = payload.ToArray();
                Ui.Post(() =>
                {
                    try
                    {
                        var bitmap = new Bitmap(new MemoryStream(bmp));
                        var (_, fingerprint) = Dib(bitmap);
                        _ = SetLocalAsync(clipboard => clipboard.SetBitmapAsync(bitmap), fingerprint, "Bild aus der Sitzung in der Zwischenablage");
                    }
                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
                    {
                        Log.Warning($"Session {_connection.Id}: a picture from the session could not be read: {exception.Message}");
                    }
                });
                break;
            case "files" when data["files"] is JsonArray files && data["token"]?.GetValue<string>() is { Length: > 0 } token:
                var list = files.OfType<JsonObject>()
                    .Select(file => new RemoteClipFile(file["name"]?.GetValue<string>() ?? "", file["size"]?.GetValue<long>() ?? 0, file["folder"]?.GetValue<bool>() ?? false))
                    .ToList();
                Ui.Post(() => _ = FetchAsync(token, list));
                break;
        }
    }

    private sealed record RemoteClipFile(string Name, long Size, bool Folder);

    private async Task SetLocalAsync(Func<IClipboard, Task> set, string fingerprint, string note)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            _clipFingerprint = fingerprint;
            await set(clipboard);
            ClipboardNote = note;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException)
        {
            Log.Warning($"Session {_connection.Id}: clipboard from the session: {exception.Message}");
        }
    }

    /// <summary>
    /// Files copied on the remote desktop: fetched into a folder of ThixGate's (as they are named there, folders too) and then put on this
    /// computer's clipboard, so Explorer / Finder pastes them. A newer copy there stops an older fetch.
    /// </summary>
    private async Task FetchAsync(string token, IReadOnlyList<RemoteClipFile> files)
    {
        _fetching?.Cancel();
        var stop = new CancellationTokenSource();
        _fetching = stop;
        var total = files.Where(file => !file.Folder).Sum(file => file.Size);
        if (total > MaxFetch || _backend is not { } backend)
        {
            ClipboardNote = "Die kopierten Dateien sind zu groß für die Zwischenablage (über 2 GB) – bitte ein freigegebenes Laufwerk nehmen.";
            _state.Text = $"{ClipboardNote}";
            return;
        }

        var folder = Path.Combine(AppPaths.TemporaryDirectory, "clipboard", token);
        var tops = new List<string>();
        long done = 0;
        try
        {
            Directory.CreateDirectory(folder);
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                var relative = string.Join(Path.DirectorySeparatorChar, file.Name.Split('/', '\\').Where(part => part is not ("" or "." or "..")));
                if (relative.Length == 0)
                {
                    continue;
                }

                var path = Path.Combine(folder, relative);
                if (!relative.Contains(Path.DirectorySeparatorChar))
                {
                    tops.Add(path);
                }

                if (file.Folder)
                {
                    Directory.CreateDirectory(path);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                long offset = 0;
                while (offset < file.Size)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var piece = await backend.ClipReadAsync(token, index, offset, stop.Token);
                    if (piece.Data.Length == 0)
                    {
                        break;
                    }

                    await output.WriteAsync(piece.Data, stop.Token);
                    offset += piece.Data.Length;
                    done += piece.Data.Length;
                    _state.Text = $"Hole {files.Count(entry => !entry.Folder)} Datei(en) aus der Sitzung … {(total == 0 ? 100 : done * 100 / total)} %";
                }
            }

            if (TopLevel.GetTopLevel(this) is not { Clipboard: { } clipboard, StorageProvider: { } storage })
            {
                return;
            }

            var items = new List<IStorageItem>();
            foreach (var top in tops.Distinct())
            {
                IStorageItem? item = Directory.Exists(top) ? await storage.TryGetFolderFromPathAsync(top) : await storage.TryGetFileFromPathAsync(top);
                if (item is not null)
                {
                    items.Add(item);
                }
            }

            _clipFingerprint = "files:" + string.Join("|", items.Select(item => item.TryGetLocalPath()));
            await clipboard.SetFilesAsync(items);
            ClipboardNote = items.Count == 1 ? "1 Datei aus der Sitzung in der Zwischenablage" : $"{items.Count} Dateien aus der Sitzung in der Zwischenablage";
            _state.Text = $"{ClipboardNote}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is ScreenException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ClipboardNote = "Die Dateien aus der Sitzung konnten nicht geholt werden: " + exception.Message;
            _state.Text = $"{ClipboardNote}";
            Log.Warning($"Session {_connection.Id}: {ClipboardNote}");
        }
    }

    /// <summary>A picture as a Windows bitmap without its file header (CF_DIB: BITMAPINFOHEADER, 32 bits, rows bottom up) and its fingerprint.</summary>
    private static (byte[] Dib, string Fingerprint) Dib(Bitmap bitmap)
    {
        var (width, height) = (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        using var copy = new WriteableBitmap(bitmap.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        var pixels = new byte[width * height * 4];
        using (var target = copy.Lock())
        {
            bitmap.CopyPixels(target, AlphaFormat.Unpremul);
            for (var row = 0; row < height; row++)
            {
                System.Runtime.InteropServices.Marshal.Copy(target.Address + row * target.RowBytes, pixels, (height - 1 - row) * width * 4, width * 4);
            }
        }

        var dib = new byte[40 + pixels.Length];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), height);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14), 32);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(20), pixels.Length);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(24), 2835);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(28), 2835);
        pixels.CopyTo(dib, 40);
        return (dib, $"image:{width}x{height}:{Convert.ToHexString(SHA256.HashData(pixels))}");
    }

    /// <summary>The window came to the front again: whatever was copied meanwhile goes to the session.</summary>
    private void WindowActivated(object? sender, EventArgs e)
    {
        if (IsEffectivelyVisible)
        {
            Dispatcher.UIThread.Post(() => _ = SyncClipboardAsync());
        }
    }
}
