using ThixGate.Core.Business;
using ThixGate.Core.Models;
using ThixGate.Core.Terminal;

namespace ThixGate.Tests;

/// <summary>ThixGate's own terminal (what an xterm reads) and the host keys of SSH servers.</summary>
public sealed class TerminalTests
{
    [Fact]
    public void Text_lines_wrapping_and_scrollback()
    {
        var buffer = new TerminalBuffer(10, 3);
        buffer.Feed("eins\r\nzwei\r\ndrei\r\nvier");
        Assert.Equal("zwei\ndrei\nvier", buffer.ScreenText());
        Assert.Equal(1, buffer.ScrollbackCount);
        Assert.Equal("eins", new string(buffer.Row(-1).Select(cell => cell.Char).ToArray()).TrimEnd());

        buffer.Feed("\r\n0123456789ab");
        Assert.Equal("0123456789", buffer.Text(1));
        Assert.True(buffer.IsWrapped(1));
        Assert.Equal("ab", buffer.Text(2));
        Assert.Equal((2, 2), (buffer.CursorColumn, buffer.CursorRow));
    }

    [Fact]
    public void Utf8_split_across_reads_and_wide_characters()
    {
        var buffer = new TerminalBuffer(10, 2);
        var bytes = System.Text.Encoding.UTF8.GetBytes("Grüße 日本");
        buffer.Feed(bytes.AsSpan(0, 3));
        buffer.Feed(bytes.AsSpan(3));
        Assert.Equal("Grüße 日本", buffer.Text(0));
        Assert.Equal(9, buffer.CursorColumn); // the wide characters took two cells each: the line is full
        Assert.Equal('\0', buffer.Row(0)[7].Char);
    }

    [Fact]
    public void Colours_and_styles()
    {
        var buffer = new TerminalBuffer(20, 2);
        buffer.Feed("\u001b[1;31mA\u001b[38;5;208mB\u001b[38;2;10;20;30;48;5;4mC\u001b[0;7mD\u001b[mE");
        var row = buffer.Row(0);
        Assert.Equal((1, CellStyle.Bold), (row[0].Foreground, row[0].Style));
        Assert.Equal(208, row[1].Foreground);
        Assert.Equal(TerminalCell.TrueColor | 0x0A141E, row[2].Foreground);
        Assert.Equal(4, row[2].Background);
        Assert.Equal(CellStyle.Inverse, row[3].Style);
        Assert.Equal((TerminalCell.Default, CellStyle.None), (row[4].Foreground, row[4].Style));
        Assert.Equal(0xE74856u, TerminalPalette.Rgb(1, isForeground: true, bold: true));
        Assert.Equal(0xFF8700u, TerminalPalette.Rgb(208, isForeground: true));
    }

    [Fact]
    public void Cursor_movement_and_erasing()
    {
        var buffer = new TerminalBuffer(10, 4);
        buffer.Feed("abcdefghij\r\nklmnopqrst\r\nuvwxyz");
        buffer.Feed("\u001b[2;3H\u001b[K");
        Assert.Equal("kl", buffer.Text(1));
        buffer.Feed("\u001b[1;5H\u001b[2P");
        Assert.Equal("abcdghij", buffer.Text(0));
        buffer.Feed("\u001b[1;1H\u001b[2@");
        Assert.Equal("  abcdghij", buffer.Text(0).PadLeft(10));
        buffer.Feed("\u001b[2J");
        Assert.Equal("", buffer.ScreenText());
        buffer.Feed("\u001b[3;4Hx\u001b[A\u001b[2Dy");
        Assert.Equal("  y", buffer.Text(1));
        Assert.Equal("   x", buffer.Text(2));
    }

    [Fact]
    public void Scroll_region_insert_and_delete_lines()
    {
        var buffer = new TerminalBuffer(5, 5);
        buffer.Feed("1\r\n2\r\n3\r\n4\r\n5");
        buffer.Feed("\u001b[2;4r\u001b[4;1H\n");
        Assert.Equal("1\n3\n4\n\n5", buffer.ScreenText());
        Assert.Equal(0, buffer.ScrollbackCount); // scrolling inside a region keeps nothing
        buffer.Feed("\u001b[2;1H\u001b[L");
        Assert.Equal("1\n\n3\n4\n5", buffer.ScreenText());
        buffer.Feed("\u001b[M");
        Assert.Equal("1\n3\n4\n\n5", buffer.ScreenText());
    }

    [Fact]
    public void The_alternate_screen_of_full_screen_programs_leaves_the_shell_as_it_was()
    {
        var buffer = new TerminalBuffer(10, 3);
        buffer.Feed("$ less x");
        buffer.Feed("\u001b[?1049h\u001b[H\u001b[2Jseite 1\u001b[?1h\u001b[?25l");
        Assert.True(buffer.IsAlternateScreen);
        Assert.True(buffer.ApplicationCursorKeys);
        Assert.False(buffer.CursorVisible);
        Assert.Equal("seite 1", buffer.Text(0));
        buffer.Feed("\u001b[?1049l\u001b[?1l\u001b[?25h");
        Assert.False(buffer.IsAlternateScreen);
        Assert.Equal("$ less x", buffer.Text(0));
        Assert.Equal((8, 0), (buffer.CursorColumn, buffer.CursorRow));
    }

    [Fact]
    public void Answers_questions_of_the_server_and_reads_titles_and_line_drawing()
    {
        var buffer = new TerminalBuffer(20, 5);
        var replies = new List<string>();
        buffer.Reply += replies.Add;
        buffer.Feed("\u001b[3;7H\u001b[6n\u001b[c\u001b]0;root@server: ~\u0007\u001b(0qx\u001b(B");
        Assert.Equal("\u001b[3;7R", replies[0]);
        Assert.StartsWith("\u001b[?", replies[1]);
        Assert.Equal("root@server: ~", buffer.Title);
        Assert.Equal("─│", buffer.Text(2).Trim());
    }

    [Fact]
    public void Resize_keeps_the_cursor_line_on_screen()
    {
        var buffer = new TerminalBuffer(10, 4);
        buffer.Feed("a\r\nb\r\nc\r\nd");
        buffer.Resize(6, 2);
        Assert.Equal("c\nd", buffer.ScreenText());
        Assert.Equal(2, buffer.ScrollbackCount);
        Assert.Equal(1, buffer.CursorRow);
        buffer.Resize(12, 6);
        Assert.Equal(12, buffer.Row(0).Length);
    }

    [Fact]
    public void Host_keys_are_asked_once_and_a_changed_key_is_noticed()
    {
        using var data = new TestData();
        Assert.Equal(HostKeyCheck.New, SshHostKeys.Check("Server.example", 22, "ssh-ed25519", "SHA256:aaa", out var known));
        Assert.Null(known);
        SshHostKeys.Trust("Server.example", 22, "ssh-ed25519", "SHA256:aaa");
        Assert.Equal(HostKeyCheck.Known, SshHostKeys.Check("server.example", 22, "ssh-ed25519", "SHA256:aaa", out _));
        Assert.Equal(HostKeyCheck.Changed, SshHostKeys.Check("server.example", 22, "ssh-ed25519", "SHA256:bbb", out known));
        Assert.Equal("SHA256:aaa", known!.Fingerprint);
        Assert.Equal(HostKeyCheck.New, SshHostKeys.Check("server.example", 2222, "ssh-ed25519", "SHA256:aaa", out _));
        SshHostKeys.Forget("server.example", 22);
        Assert.Equal(HostKeyCheck.New, SshHostKeys.Check("server.example", 22, "ssh-ed25519", "SHA256:aaa", out _));
    }

    [Fact]
    public async Task An_unreachable_server_is_a_message_not_a_crash()
    {
        using var data = new TestData();
        var connection = new RemoteConnection { Name = "Nirgends", Protocol = ConnectionProtocol.Ssh, Host = "127.0.0.1", Port = 1, UserName = "demo" };
        await using var session = new SshSession(connection, new NoHost());
        var error = await Assert.ThrowsAsync<BusinessException>(() => session.ConnectAsync(80, 24, CancellationToken.None));
        Assert.Contains("nicht erreichbar", error.Message);
    }

    /// <summary>
    /// Opt-in: a real SSH server, e.g. Matgate's test one: docker run -d -p 12222:2222 -e USER_NAME=demo -e USER_PASSWORD=demo-only-pw
    /// -e PASSWORD_ACCESS=true lscr.io/linuxserver/openssh-server, then THIXGATE_E2E_SSH=127.0.0.1:12222 (test credentials of that container).
    /// </summary>
    [Fact]
    public async Task Real_server_signs_in_runs_a_command_and_keeps_its_key()
    {
        if (Environment.GetEnvironmentVariable("THIXGATE_E2E_SSH") is not { } address)
        {
            return;
        }

        using var data = new TestData();
        var (host, port) = (address.Split(':')[0], int.Parse(address.Split(':')[1]));
        var credential = CredentialService.Save(new StoredCredential { Name = "Test", UserName = "demo" }, "demo-only-pw");
        var connection = LibraryService.SaveConnection(new RemoteConnection { Name = "SSH-Test", Protocol = ConnectionProtocol.Ssh, Host = host, Port = port, CredentialId = credential.Id });
        var asker = new NoHost { Trust = true };
        var buffer = new TerminalBuffer(80, 24);
        await using (var session = new SshSession(connection, asker))
        {
            var received = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
            session.Received += received.Enqueue;
            await session.ConnectAsync(80, 24, CancellationToken.None);
            session.Send("echo thix$((40+2))gate\r");
            for (var until = DateTime.UtcNow.AddSeconds(15); DateTime.UtcNow < until && !buffer.ScreenText().Contains("thix42gate", StringComparison.Ordinal);)
            {
                while (received.TryDequeue(out var bytes))
                {
                    buffer.Feed(bytes);
                }

                await Task.Delay(50);
            }

            Assert.Contains("thix42gate", buffer.ScreenText());
            Assert.Equal(1, asker.HostKeyQuestions);
            Assert.Equal(0, asker.PasswordQuestions); // the vault had it
        }

        await using (var again = new SshSession(connection, asker))
        {
            await again.ConnectAsync(80, 24, CancellationToken.None);
            Assert.Equal(1, asker.HostKeyQuestions); // known now
        }
    }

    private sealed class NoHost : ISshHost
    {
        public bool Trust { get; init; }

        public int HostKeyQuestions { get; private set; }

        public int PasswordQuestions { get; private set; }

        public Task<SshAnswer?> AskAsync(string userName, string prompt, bool failed, bool canRemember)
        {
            PasswordQuestions++;
            return Task.FromResult<SshAnswer?>(null);
        }

        public Task<bool> TrustHostKeyAsync(string host, int port, string algorithm, string fingerprint, KnownHost? previous)
        {
            HostKeyQuestions++;
            return Task.FromResult(Trust);
        }
    }
}
