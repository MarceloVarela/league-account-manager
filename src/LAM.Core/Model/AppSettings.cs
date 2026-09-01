namespace LAM.Core.Model;

public enum StrategyPreference
{
    /// <summary>Swap a saved session when one is usable, otherwise type. The default, and the point
    /// of the design: typing happens once per account and then effectively never again.</summary>
    PreferSessionSwap,

    /// <summary>Always type the credentials. Use if session swapping misbehaves.</summary>
    AlwaysAutofill,

    /// <summary>Only ever swap. Fails rather than typing — for when you would rather not have
    /// keystroke injection happen at all, at the cost of manual re-capture when sessions expire.</summary>
    SessionSwapOnly,
}

public sealed class AppSettings
{

    /// <summary>
    /// Hide account names automatically while capture software is running.
    ///
    /// On by default: the failure mode of forgetting to enable it is a Riot ID on a stream, and the
    /// failure mode of it being on unnecessarily is a few dots on your own screen.
    /// </summary>
    public bool RedactWhileStreaming { get; set; } = true;
    /// <summary>Minutes of inactivity before the vault re-locks. Zero disables the idle timer.</summary>
    public int AutoLockMinutes { get; set; } = 5;

    public bool LockOnWorkstationLock { get; set; } = true;

    public bool LockOnMinimize { get; set; }

    public StrategyPreference Strategy { get; set; } = StrategyPreference.PreferSessionSwap;

    /// <summary>
    /// Riot API key for rank and level. Use a Personal key — Development keys expire every 24
    /// hours, which would mean re-pasting it daily.
    /// </summary>
    public SecretText? RiotApiKey { get; set; }

    /// <summary>Overrides the path discovered from RiotClientInstalls.json. Normally null.</summary>
    public string? RiotClientPathOverride { get; set; }

    public bool WindowsHelloEnabled { get; set; }

    /// <summary>How many rolling vault backups to retain.</summary>
    public int BackupsToKeep { get; set; } = 10;

    /// <summary>Refuse to type if the foreground window changes mid-sequence. Effectively mandatory:
    /// turning it off is how a password ends up typed into whatever you alt-tabbed to.</summary>
    public bool AbortTypingOnFocusLoss { get; set; } = true;

    /// <summary>
    /// Press Play once signed in, so the game actually starts.
    ///
    /// Also what lets the app read the account's Riot ID, level and PUUID: those come from the
    /// League client, which does not exist until Play is pressed.
    /// </summary>
    public bool LaunchGameAfterSignIn { get; set; } = true;

    /// <summary>
    /// Restore your saved League settings after each sign-in.
    ///
    /// Off by default because it writes into the game's own configuration. Losing somebody's
    /// keybinds would be far worse than not carrying them, so this is opted into deliberately.
    /// </summary>
    public bool CarryGameSettings { get; set; }

    /// <summary>Seconds to wait for the Riot Client login form before giving up.</summary>
    public int LoginWindowTimeoutSeconds { get; set; } = 60;
}
