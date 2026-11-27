using Matgate.Files;
using Matgate.Models;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

/// <summary>What the user typed when a connection asked to sign in.</summary>
public sealed record LoginAnswer(string UserName, string Password, bool Remember, string Domain = "");

/// <summary>Asks the user to sign in (any thread): null = cancelled.</summary>
public delegate Task<LoginAnswer?> AskSignIn(string userName, bool failed, bool canRemember);

/// <summary>
/// A file connection (SFTP, FTP, SMB, WebDAV) as the file manager tab uses it: Mat.Remote's <see cref="RemoteFiles"/> and
/// <see cref="FileOperations"/> - the very code of Matgate's file manager - with ThixGate's login (vault or asked) and, über Matgate,
/// its relay. Every operation opens its own connection to the server (as in Matgate); local files come and go only through here.
/// </summary>
public sealed class FileSession
{
    private const int Attempts = 3;

    private FileSession(RemoteConnection connection, FileTarget target)
    {
        Connection = connection;
        Target = target;
        Store = new RemoteFileStore(target);
    }

    public RemoteConnection Connection { get; }

    public FileTarget Target { get; }

    public IFileStore Store { get; }

    /// <summary>Signs in (asking when the vault has no password or it is refused) and lists the top once.</summary>
    public static async Task<FileSession> OpenAsync(RemoteConnection connection, AskSignIn ask, CancellationToken cancellationToken)
    {
        var login = SessionTargets.LoginOf(connection);
        var userName = login.UserName;
        var password = login.Password;
        var canRemember = login.CredentialId is not null && !AccessPolicy.ArePasswordsDisabled;
        LoginAnswer? typed = null;
        var refused = false;
        for (var attempt = 0; ; attempt++)
        {
            // FTP may be anonymous; the others need a user and a password or key - asked before the first try when the vault has none.
            var mustAsk = refused || (connection.Protocol != ConnectionProtocol.Ftp && (userName.Length == 0 || (password.Length == 0 && !SshKeys.Has(connection))));
            if (mustAsk)
            {
                typed = await ask(userName, refused, canRemember) ?? throw new OperationCanceledException(cancellationToken);
                (userName, password) = (typed.UserName.Trim(), typed.Password);
            }

            var target = await SessionTargets.FileTargetAsync(connection, userName.Length > 0 ? userName : "anonymous", password, cancellationToken, typed?.Domain ?? "");
            var session = new FileSession(connection, target);
            try
            {
                await RemoteFiles.ListAsync(target, "/", cancellationToken);
                if (typed is { Remember: true, Password.Length: > 0 } && login.CredentialId is { } credentialId)
                {
                    CredentialService.RememberPassword(credentialId, typed.Password);
                }

                LibraryService.MarkUsed(connection.Id);
                return session;
            }
            catch (Exception exception) when (IsSignInRefused(exception) && attempt + 1 < Attempts)
            {
                Log.Info($"Files {connection.Id}: sign-in refused ({exception.Message})");
                refused = true;
                password = "";
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not BusinessException)
            {
                throw new BusinessException(Message(connection, exception));
            }
        }
    }

    /// <summary>A message for the user from what the server or the client said.</summary>
    public static string Message(RemoteConnection connection, Exception exception) => exception switch
    {
        System.Net.Sockets.SocketException => $"{connection.Host}:{connection.EffectivePort} ist nicht erreichbar ({exception.Message}).",
        _ when IsSignInRefused(exception) => "Die Anmeldung wurde abgelehnt: Benutzer, Passwort oder Schlüssel stimmen nicht.",
        _ => exception.Message,
    };

    private static bool IsSignInRefused(Exception exception) =>
        exception is Renci.SshNet.Common.SshAuthenticationException
        || exception.Message.Contains("STATUS_LOGON_FAILURE", StringComparison.Ordinal)
        || exception.Message.Contains("STATUS_ACCESS_DENIED", StringComparison.Ordinal) && exception.Message.Contains("Anmeldung", StringComparison.Ordinal)
        || exception.Message.Contains("401", StringComparison.Ordinal)
        || exception is FluentFTP.Exceptions.FtpAuthenticationException;

    // ================================================================= reading

    public async Task<FileGatewayListResult> ListAsync(string path, CancellationToken cancellationToken) =>
        await Guard(() => RemoteFiles.ListAsync(Target, path, cancellationToken));

    /// <summary>Downloads a file into a local file (the caller chose it).</summary>
    public async Task DownloadAsync(string path, string localFile, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        await Guard(async () =>
        {
            var download = await RemoteFiles.DownloadAsync(Target, path, cancellationToken);
            await using (download.Content)
            await using (var file = File.Create(localFile))
            {
                await CopyAsync(download.Content, file, progress, cancellationToken);
            }

            return 0;
        });
    }

    /// <summary>Downloads a folder with everything in it into a local folder.</summary>
    public async Task<int> DownloadFolderAsync(string path, string localFolder, CancellationToken cancellationToken)
    {
        var count = 0;
        async Task Walk(string remote, string local, int depth)
        {
            if (depth > 32 || count > FileOperations.MaxEntries)
            {
                throw new BusinessException("Zu viele Dateien auf einmal.");
            }

            Directory.CreateDirectory(local);
            foreach (var entry in (await ListAsync(remote, cancellationToken)).Entries)
            {
                var target = Path.Combine(local, RemoteFiles.SafeFileName(entry.Name));
                if (entry.IsDirectory)
                {
                    await Walk(entry.Path, target, depth + 1);
                }
                else
                {
                    await DownloadAsync(entry.Path, target, null, cancellationToken);
                    count++;
                }
            }
        }

        await Walk(path, localFolder, 0);
        return count;
    }

    /// <summary>The selection as one ZIP file on this computer.</summary>
    public async Task DownloadZipAsync(IReadOnlyList<string> paths, string localFile, CancellationToken cancellationToken) =>
        await Guard(async () =>
        {
            await using var file = File.Create(localFile);
            await FileOperations.ZipAsync(Store, paths, file, cancellationToken);
            return 0;
        });

    /// <summary>A file into a temporary file of this computer, to open it with its program or to preview it.</summary>
    public async Task<string> DownloadTemporaryAsync(string path, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(AppPaths.TemporaryDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var local = Path.Combine(folder, RemoteFiles.SafeFileName(RemoteFiles.FileNameFromPath(path)));
        await DownloadAsync(path, local, null, cancellationToken);
        return local;
    }

    // ================================================================= writing

    public async Task UploadAsync(string folder, string localFile, IProgress<long>? progress, CancellationToken cancellationToken) =>
        await Guard(async () =>
        {
            await using var file = File.OpenRead(localFile);
            await using var counting = new ProgressStream(file, progress);
            await RemoteFiles.UploadAsync(Target, folder, counting, Path.GetFileName(localFile), cancellationToken);
            return 0;
        });

    /// <summary>A local folder with everything in it (Matgate's page cannot do that, the desktop can).</summary>
    public async Task<int> UploadFolderAsync(string folder, string localFolder, CancellationToken cancellationToken) =>
        await Guard(async () =>
        {
            var name = RemoteFiles.SafeFileName(Path.GetFileName(Path.TrimEndingDirectorySeparator(localFolder)));
            var existing = (await RemoteFiles.ListAsync(Target, folder, cancellationToken)).Entries;
            if (!existing.Any(entry => entry.IsDirectory && entry.Name == name))
            {
                await RemoteFiles.CreateDirectoryAsync(Target, folder, name, cancellationToken);
            }

            return await FileOperations.UploadTreeAsync(Store, localFolder, RemoteFiles.CombineVirtualPath(RemoteFiles.NormalizeVirtualPath(folder), name), cancellationToken);
        });

    /// <summary>Uploads an archive and unpacks it there (Matgate's "unzip while uploading").</summary>
    public async Task<int> UploadAndExtractAsync(string folder, string localArchive, CancellationToken cancellationToken) =>
        await Guard(async () =>
        {
            var temp = new LocalStore(Path.GetDirectoryName(localArchive)!);
            return await FileOperations.ExtractAsync(new SplitStore(temp, Store), "/" + Path.GetFileName(localArchive), folder, cancellationToken);
        });

    public async Task CreateFolderAsync(string folder, string name, CancellationToken cancellationToken) =>
        await Guard(async () => { await RemoteFiles.CreateDirectoryAsync(Target, folder, Name(name), cancellationToken); return 0; });

    public async Task CreateFileAsync(string folder, string name, CancellationToken cancellationToken) =>
        await Guard(async () => { await RemoteFiles.CreateFileAsync(Target, folder, Name(name), cancellationToken); return 0; });

    public async Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        await Guard(async () =>
        {
            foreach (var path in paths)
            {
                await RemoteFiles.DeleteAsync(Target, path, cancellationToken);
            }

            return 0;
        });

    public async Task RenameAsync(string path, string newName, CancellationToken cancellationToken) =>
        await Guard(async () => { await FileOperations.RenameAsync(Store, path, Name(newName), cancellationToken); return 0; });

    /// <summary>Copies (or moves) the selection into a folder of this or another file session.</summary>
    public async Task<FileCopyResult> CopyToAsync(IReadOnlyList<string> paths, FileSession target, string folder, bool move, CancellationToken cancellationToken) =>
        await Guard(() =>
        {
            var same = ReferenceEquals(this, target) || Target == target.Target;
            return move
                ? FileOperations.MoveAsync(Store, paths, target.Store, folder, same, cancellationToken)
                : FileOperations.CopyAsync(Store, paths, target.Store, folder, same, cancellationToken: cancellationToken);
        });

    public async Task<string> ZipIntoAsync(IReadOnlyList<string> paths, string folder, string name, CancellationToken cancellationToken) =>
        await Guard(() => FileOperations.ZipIntoAsync(Store, paths, folder, Name(name), cancellationToken));

    public async Task<int> ExtractAsync(string archivePath, string folder, CancellationToken cancellationToken) =>
        await Guard(() => FileOperations.ExtractAsync(Store, archivePath, folder, cancellationToken));

    // ================================================================= helpers

    private static string Name(string name)
    {
        var cleaned = name.Trim();
        if (cleaned.Length == 0 || cleaned is "." or ".." || cleaned.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new BusinessException("Bitte einen Namen ohne / und \\ angeben.", "Name");
        }

        return cleaned;
    }

    /// <summary>What a client throws becomes a message for the user.</summary>
    private async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not BusinessException)
        {
            Log.Warning($"Files {Connection.Id}: {exception.GetType().Name}: {exception.Message}");
            throw new BusinessException(Message(Connection, exception));
        }
    }

    private static async Task CopyAsync(Stream from, Stream to, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[256 * 1024];
        long total = 0;
        int read;
        while ((read = await from.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
            progress?.Report(total);
        }
    }

    /// <summary>Reports how much was read (uploads).</summary>
    private sealed class ProgressStream(Stream inner, IProgress<long>? progress) : Stream
    {
        private long _total;

        public override bool CanRead => true;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Report(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Report(read);
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void Report(int read)
        {
            _total += read;
            progress?.Report(_total);
        }
    }

    /// <summary>A local folder as a store (the archive to unpack while uploading).</summary>
    private sealed class LocalStore(string root) : IFileStore
    {
        public Task<FileGatewayListResult> ListAsync(string? path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FileGatewayDownload> DownloadAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new FileGatewayDownload(File.OpenRead(Path.Combine(root, Path.GetFileName(path))), Path.GetFileName(path), "application/octet-stream"));

        public Task UploadAsync(string folder, Stream content, string fileName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CreateDirectoryAsync(string folder, string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>Reads from one store, writes to another (unpack a local archive into the connection).</summary>
    private sealed class SplitStore(IFileStore read, IFileStore write) : IFileStore
    {
        public Task<FileGatewayListResult> ListAsync(string? path, CancellationToken cancellationToken) => write.ListAsync(path, cancellationToken);

        public Task<FileGatewayDownload> DownloadAsync(string path, CancellationToken cancellationToken) => read.DownloadAsync(path, cancellationToken);

        public Task UploadAsync(string folder, Stream content, string fileName, CancellationToken cancellationToken) => write.UploadAsync(folder, content, fileName, cancellationToken);

        public Task CreateDirectoryAsync(string folder, string name, CancellationToken cancellationToken) => write.CreateDirectoryAsync(folder, name, cancellationToken);

        public Task DeleteAsync(string path, CancellationToken cancellationToken) => write.DeleteAsync(path, cancellationToken);
    }
}
