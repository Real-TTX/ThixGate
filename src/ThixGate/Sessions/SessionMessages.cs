namespace ThixGate.Sessions;

/// <summary>
/// The engine (Mat.Remote) says what went wrong in English; the session shows the common ones in German. A message it does not know
/// stays as it is (with its details, which help more than a vague German sentence).
/// </summary>
internal static class SessionMessages
{
    private static readonly (string English, string German)[] Known =
    [
        ("The RDP server could not be reached.", "Der Remote-Desktop ist nicht erreichbar."),
        ("The RDP server did not answer in time.", "Der Remote-Desktop hat nicht rechtzeitig geantwortet."),
        ("The RDP server refused the connection.", "Der Remote-Desktop hat die Verbindung abgelehnt."),
        ("The host name could not be resolved.", "Den Rechnernamen gibt es nicht (DNS)."),
        ("The server did not answer like an RDP server.", "Unter dieser Adresse antwortet kein Remote-Desktop."),
        ("The RDP server wants a user name and password before it shows anything.", "Der Remote-Desktop will zuerst Benutzer und Passwort."),
        ("This user is not allowed to sign in over Remote Desktop on this server.", "Dieser Benutzer darf sich auf diesem Server nicht per Remote Desktop anmelden."),
        ("The connection was cancelled.", "Die Verbindung wurde abgebrochen."),
        ("The login was cancelled.", "Die Anmeldung wurde abgebrochen."),
        ("The password was refused.", "Das Passwort wurde abgelehnt."),
        ("The connection to the VNC server was lost.", "Die Verbindung zum VNC-Server ist abgerissen."),
        ("The RDP server showed no certificate.", "Der Remote-Desktop hat kein Zertifikat gezeigt."),
        ("The RDP server has no certificate for a secure connection.", "Der Remote-Desktop hat kein Zertifikat für eine sichere Verbindung."),
    ];

    public static string German(string message)
    {
        foreach (var (english, german) in Known)
        {
            if (message.StartsWith(english, StringComparison.Ordinal))
            {
                return german + message[english.Length..];
            }
        }

        return message;
    }
}
