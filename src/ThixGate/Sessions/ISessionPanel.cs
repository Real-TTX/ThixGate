using Avalonia.Controls;
using ThixGate.Core.Business;

namespace ThixGate.Sessions;

/// <summary>
/// The content of a session tab that is not a picture or a terminal: the file manager, the database browser, the browser of a website.
/// <see cref="SessionView"/> gives it the bar (state, Vollbild, Abdocken, Trennen) and calls it to connect and to end.
/// </summary>
internal interface ISessionPanel
{
    Control View { get; }

    /// <summary>Connects and signs in (asking through <paramref name="ask"/>); throws a BusinessException with the message for the user.</summary>
    Task ConnectAsync(AskSignIn ask, CancellationToken cancellationToken);

    /// <summary>The session ends (Trennen, tab closed): forget the login, stop what runs.</summary>
    void Disconnect();

    /// <summary>Puts the keyboard into the panel.</summary>
    void FocusPanel();
}
