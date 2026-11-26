using System.Text.Json.Nodes;
using Matgate.Display;

namespace ThixGate.Sessions;

/// <summary>A host that shows nothing and answers no question (self test: the engine's backends are created and closed without a screen).</summary>
internal sealed class NullScreenHost : IScreenHost
{
    public void Dirty(int x, int y, int width, int height)
    {
    }

    public void Cursor(int hotX, int hotY, int width, int height, ReadOnlySpan<byte> rgba)
    {
    }

    public void CursorHidden(bool hidden)
    {
    }

    public void CursorDefault()
    {
    }

    public void Event(string op, JsonObject data, ReadOnlyMemory<byte> payload = default)
    {
    }

    public void Status(string state, string? message = null)
    {
    }

    public Task<ScreenCredentials?> AskAsync(string[] need, string userName, string domain, bool failed, CancellationToken cancellationToken) =>
        Task.FromResult<ScreenCredentials?>(null);

    public void Audio(ReadOnlySpan<byte> pcm, int rate, int channels)
    {
    }

    public (int Width, int Height) WantedSize() => (0, 0);

    public void Log(string message)
    {
    }
}
