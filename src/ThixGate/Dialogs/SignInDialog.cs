using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;
using ThixGate.Controls;

namespace ThixGate.Dialogs;

/// <summary>What the person typed in the sign-in dialog.</summary>
public sealed record SignInAnswer(string UserName, string Password, string Domain, bool Remember);

/// <summary>
/// Asks for what the server needs to sign in (user, password, domain - only the fields asked for), at connect or after a failed attempt.
/// "Passwort speichern" is offered when the connection uses saved credentials and passwords may be stored.
/// </summary>
public sealed class SignInDialog : Window
{
    private readonly TaskCompletionSource<SignInAnswer?> _answer = new();
    private readonly TextBox _user = new();
    private readonly TextBox _password = new() { PasswordChar = '●' };
    private readonly TextBox _domain = new();
    private readonly CheckBox _remember = new() { Content = "Passwort in den Zugangsdaten speichern" };

    public SignInDialog(string connectionName, string[] need, string userName, string domain, bool failed, bool canRemember)
    {
        Title = $"Anmelden – {connectionName}";
        Icon = Ui.AppIcon;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Use(BackgroundProperty, "Mat.Surface");

        _user.Text = userName;
        _domain.Text = domain;
        AutomationProperties.SetName(_user, "Benutzername");
        AutomationProperties.SetName(_password, "Passwort");
        AutomationProperties.SetName(_domain, "Domäne");
        _remember.IsVisible = canRemember;

        var ok = Ui.Button("login", "Anmelden", "MatButton.Primary");
        ok.IsDefault = true;
        var cancel = Ui.Button("x", "Abbrechen");
        cancel.IsCancel = true;
        ok.Click += (_, _) => Finish(new SignInAnswer(_user.Text ?? "", _password.Text ?? "", _domain.Text ?? "", _remember.IsChecked == true));
        cancel.Click += (_, _) => Finish(null);
        Closed += (_, _) => _answer.TrySetResult(null);

        var info = new InfoBar();
        if (failed)
        {
            info.Show("Die Anmeldung wurde abgelehnt – bitte prüfe Benutzer und Passwort.", InfoSeverity.Warning);
        }

        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = "Anmelden", FontSize = MatTokens.Type.FsXl, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = $"{connectionName} möchte wissen, wer sich anmeldet.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) }.Themed("MatText.Muted"));
        panel.Children.Add(info);
        if (need.Contains("username") || userName.Length == 0)
        {
            panel.Children.Add(new MatFormField { Label = "Benutzername", Content = _user });
        }

        if (need.Contains("domain"))
        {
            panel.Children.Add(new MatFormField { Label = "Domäne", Content = _domain, Hint = "Leer bei lokalen Konten" });
        }

        panel.Children.Add(new MatFormField { Label = "Passwort", Content = _password });
        panel.Children.Add(_remember);
        panel.Children.Add(new ActionBar { Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } });
        Content = panel;
        Opened += (_, _) => (need.Contains("username") && userName.Length == 0 ? _user : _password).Focus();
    }

    public Task<SignInAnswer?> Answer => _answer.Task;

    public static async Task<SignInAnswer?> AskAsync(Window? owner, string connectionName, string[] need, string userName, string domain, bool failed, bool canRemember)
    {
        var dialog = new SignInDialog(connectionName, need, userName, domain, failed, canRemember);
        if (owner is not null)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }

        return await dialog.Answer;
    }

    private void Finish(SignInAnswer? answer)
    {
        _answer.TrySetResult(answer);
        Close();
    }
}
