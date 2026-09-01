using System.Text.Json;

namespace LAM.Core.Login;

/// <summary>
/// The hints used to recognise parts of the Riot Client login page, and the phrases used to read
/// its status messages.
///
/// All of it lives in one place, and can be overridden by dropping a <c>login-ui.json</c> beside the
/// vault, because this is the part of the app most likely to be broken by a Riot UI update. When
/// that happens it should be a data fix, not a rebuild.
/// </summary>
public sealed class LoginUiProfile
{
    /// <summary>Automation ids or control names that identify the username field.</summary>
    public List<string> UsernameHints { get; set; } = ["username", "user", "email", "login", "riot id"];

    public List<string> PasswordHints { get; set; } = ["password", "pass", "senha", "contraseña"];

    public List<string> StaySignedInHints { get; set; } =
        ["stay signed in", "keep me signed in", "remember me", "remember-me",
         "manter conectado", "permanecer conectado"];

    /// <summary>Names that identify the button which submits the form.</summary>
    public List<string> SubmitHints { get; set; } =
    [
        "sign in", "sign-in", "log in", "login", "submit", "continue", "next", "confirm",
        "entrar", "acessar", "iniciar sesion", "connexion",
    ];

    /// <summary>
    /// Buttons that must never be treated as the submit control.
    ///
    /// The login page puts these in a row directly under the password field, so a chooser that fell
    /// back to "the nearest button" would start an OAuth flow with an identity provider instead of
    /// signing in. Excluded before anything else is considered.
    /// </summary>
    public List<string> SocialProviderHints { get; set; } =
    [
        "facebook", "google", "apple", "xbox", "playstation", "psn", "nintendo",
        "qr", "qr code", "scan",
    ];

    /// <summary>Title-bar controls, which are Buttons like any other in the automation tree.</summary>
    public List<string> WindowChromeHints { get; set; } =
        ["minimize", "minimise", "maximize", "maximise", "restore", "close window", "animation"];

    /// <summary>Phrases that mean a bot check is being shown and the user has to take over.</summary>
    /// <remarks>
    /// Wording that only appears on a live challenge. Bare product names are deliberately absent:
    /// the login page permanently footers "THIS APP IS PROTECTED BY HCAPTCHA", so matching on
    /// "hcaptcha" reported a security check on every run. Detection is also diffed against a
    /// baseline of the page text taken before submitting, so static boilerplate cannot trigger it.
    /// </remarks>
    public List<string> CaptchaPhrases { get; set; } =
    [
        "verify you are human", "verify you're human",
        "i am human", "i am not a robot", "are you a robot",
        "select all images", "select each image", "click each image",
        "complete the challenge", "solve the challenge",
    ];

    /// <summary>Phrases that mean the credentials were rejected.</summary>
    public List<string> WrongCredentialPhrases { get; set; } =
    [
        "invalid username or password",
        "incorrect username or password",
        "wrong password",
        "credentials are incorrect",
        "usuário ou senha", "usuario o contraseña",
    ];

    /// <summary>Phrases that mean a verification or two-factor code is being asked for.</summary>
    public List<string> VerificationPhrases { get; set; } =
    [
        "verification code",
        "two-factor",
        "authentication code",
        "we sent a code",
        "enter the code",
        "código de verificação", "código de verificación",
    ];

    /// <summary>Phrases that mean we are being rate limited and should stop retrying.</summary>
    public List<string> RateLimitPhrases { get; set; } =
    [
        "too many",
        "try again later",
        "rate limit",
        "muitas tentativas",
    ];

    /// <summary>Seconds to wait for the accessibility tree to populate after the window appears.</summary>
    public int AccessibilityWarmupSeconds { get; set; } = 20;

    public static LoginUiProfile LoadOrDefault(string directory)
    {
        var path = Path.Combine(directory, "login-ui.json");
        if (!File.Exists(path)) return new LoginUiProfile();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<LoginUiProfile>(json, Options) ?? new LoginUiProfile();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new LoginUiProfile();
        }
    }

    /// <summary>Writes the current profile out so it can be edited when Riot changes the UI.</summary>
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "login-ui.json"), JsonSerializer.Serialize(this, Options));
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public bool MatchesAny(string? candidate, IEnumerable<string> hints)
        => !string.IsNullOrWhiteSpace(candidate)
           && hints.Any(hint => candidate.Contains(hint, StringComparison.OrdinalIgnoreCase));
}
