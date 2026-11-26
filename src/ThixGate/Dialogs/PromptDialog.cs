using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using MatDesktop;
using ThixGate.Controls;

namespace ThixGate.Dialogs;

/// <summary>Asks for one line of text (a new folder, a new name). Enter confirms, Esc cancels; the name part is selected (not the extension).</summary>
public sealed class PromptDialog : MatWindow
{
    private readonly TextBox _input = new();
    private string? _answer;

    private PromptDialog(string title, string label, string initial, string confirmText)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Use(BackgroundProperty, "Mat.Surface");

        _input.Text = initial;
        AutomationProperties.SetName(_input, label);
        var confirm = new Button { Content = new IconLabel { Icon = "check", Text = confirmText }, IsDefault = true, Theme = Ui.Theme("MatButton.Primary") };
        confirm.Click += (_, _) =>
        {
            _answer = _input.Text ?? "";
            Close();
        };
        var cancel = new Button { Content = new IconLabel { Icon = "x", Text = "Abbrechen" }, IsCancel = true };
        cancel.Click += (_, _) => Close();

        var heading = new TextBlock { Text = title, FontSize = MatTokens.Type.FsLg, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
        var field = new MatFormField { Label = label, Content = _input, Margin = new Thickness(0, 0, 0, 18) };
        Content = new StackPanel { Margin = new Thickness(24, 22), Children = { heading, field, new ActionBar { Children = { confirm, cancel } } } };
        Opened += (_, _) =>
        {
            Current = this;
            _input.Focus();
            var dot = initial.LastIndexOf('.');
            _input.SelectionStart = 0;
            _input.SelectionEnd = dot > 0 ? dot : initial.Length;
        };
        Closed += (_, _) => Current = Current == this ? null : Current;
    }

    /// <summary>The question shown now (tests answer it).</summary>
    internal static PromptDialog? Current { get; private set; }

    /// <summary>Answers as the buttons would (tests); null: cancel.</summary>
    internal void Answer(string? text)
    {
        _answer = text;
        Close();
    }

    /// <summary>The text typed; null when cancelled.</summary>
    public static async Task<string?> AskAsync(Window? owner, string title, string label, string initial = "", string confirmText = "OK")
    {
        var dialog = new PromptDialog(title, label, initial, confirmText);
        if (owner is { IsVisible: true })
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            var closed = new TaskCompletionSource();
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            await closed.Task;
        }

        return dialog._answer;
    }
}
