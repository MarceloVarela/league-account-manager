using LAM.Core.Model;
using LAM.Core.Riot;

namespace LAM.Core.Login;

/// <summary>
/// What both strategies do once the client is signed in: save the session and learn who signed in.
///
/// Shared because the capture is what makes the whole design work. Autofill types a password once
/// and captures the session it produced; the swap strategy re-captures the rotated session on every
/// use. Either way the account ends up with a fresh session, so typing stays a one-off rather than
/// something that comes back three weeks later.
/// </summary>
public sealed class PostLoginCapture
{
    private readonly RiotYamlService _yaml;
    private readonly LcuIdentityProbe _identity;
    private readonly TimeProvider _clock;
    private readonly LoginTrace _trace;
    private GameStarter? _gameStarter;
    private ClientAccountProbe? _accountProbe;
    private ClientStatsProbe? _statsProbe;
    private ClientLootProbe? _lootProbe;
    private GameSettingsProfile? _settings;

    public PostLoginCapture(
        RiotYamlService yaml,
        LcuIdentityProbe identity,
        TimeProvider? clock = null,
        LoginTrace? trace = null)
    {
        _yaml = yaml;
        _identity = identity;
        _clock = clock ?? TimeProvider.System;
        _trace = trace ?? LoginTrace.Null;
    }

    /// <summary>Supplies the component that presses Play. Set once during composition.</summary>
    public void UseGameStarter(GameStarter starter) => _gameStarter = starter;

    /// <summary>Supplies the probe that reads the recovery dossier from the client.</summary>
    public void UseAccountProbe(ClientAccountProbe probe) => _accountProbe = probe;

    /// <summary>Supplies the probe that reads rank, wallet and collection from the client.</summary>
    public void UseStatsProbe(ClientStatsProbe probe) => _statsProbe = probe;

    /// <summary>Supplies the probe that reads the loot inventory from the client.</summary>
    public void UseLootProbe(ClientLootProbe probe) => _lootProbe = probe;

    public void UseMatchProbe(ClientMatchProbe probe) => _matchProbe = probe;

    /// <summary>Supplies the component that carries League settings across accounts.</summary>
    public void UseGameSettings(GameSettingsProfile settings) => _settings = settings;

    public async Task RunAsync(LoginContext context, CancellationToken cancellationToken)
    {
        await CaptureSessionAsync(context, cancellationToken);

        // Start the game before reading identity, not after. The League client is where the PUUID,
        // Riot ID and level come from, and it does not exist until Play is pressed — waiting for it
        // first meant a two-minute stall that always ended empty-handed.
        var outcome = _gameStarter is null
            ? GameStartOutcome.Disabled
            : await _gameStarter.TryStartGameAsync(context, cancellationToken);

        await ReadIdentityAsync(context, outcome.GameIsComing(), cancellationToken);
        await ReadAccountFactsAsync(context, cancellationToken);
        await ReadClientStatsAsync(context, cancellationToken);
        await ReadLootAsync(context, cancellationToken);
        await ReadMatchesAsync(context, cancellationToken);
        RestoreGameSettings(context);
    }

    /// <summary>
    /// Saves the client's session file, once it has settled.
    ///
    /// The client writes its cookies a moment after the API reports authorised, so reading straight
    /// away catches the pre-login file and stores a session that will never work. Poll instead, and
    /// only accept a document that genuinely contains sign-in cookies.
    /// </summary>
    public async Task<bool> CaptureSessionAsync(LoginContext context, CancellationToken cancellationToken)
    {
        context.Report(LoginStage.CapturingSession, "Saving the session so next time needs no typing…");

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = _yaml.ReadPrivateSettings();
            if (RiotYamlService.ContainsSession(text))
            {
                _trace.Write("session captured — the next sign-in for this account will be instant");
                context.SessionCaptured?.Invoke(new StoredSession
                {
                    Yaml = new SecretText(text!),
                    CapturedUtc = _clock.GetUtcNow(),
                    Puuid = context.Account.Identity.RiotAccountId,
                    KnownExpired = false,
                });
                return true;
            }

            await Task.Delay(1000, cancellationToken);
        }

        // Not fatal — the account is signed in either way. It only means the next login will type
        // again, which is exactly what happens on a client that does not persist sessions at all.
        _trace.Write("NO session was captured — the next sign-in will type again");
        context.Report(LoginStage.CapturingSession,
            "Signed in, but this client stored no resumable session — the next login will type again.");
        return false;
    }

    /// <summary>
    /// Asks the League client who is signed in, so the Riot ID, level and PUUID fill themselves in.
    /// Enrichment only: a failure here must never turn a successful login into a reported failure.
    /// </summary>
    public async Task ReadIdentityAsync(
        LoginContext context, bool gameStarted, CancellationToken cancellationToken)
    {
        context.Report(LoginStage.ReadingIdentity, "Reading account details from the client…");

        // Only worth a long wait if League is on its way. When it is not, the lockfile can never
        // appear and a two-minute wait for it is pure dead time on every sign-in.
        //
        // "On its way" deliberately includes a click that has only just been accepted: a first boot
        // can take minutes, and cutting the wait short there is what previously left accounts with
        // no Riot ID after a launch that had worked.
        var timeout = gameStarted ? TimeSpan.FromSeconds(150) : TimeSpan.FromSeconds(15);

        try
        {
            var reading = await _identity.TryReadAsync(timeout, cancellationToken);
            if (reading is not null) context.IdentityObserved?.Invoke(reading);
            _trace.Write(reading is not null
                ? "identity read from the League client"
                : "could not read identity" + (gameStarted ? " (League did not finish starting in time)" : " (League was not started)"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // League may simply be slow to start, or not installed for this account's region.
        }
    }

    /// <summary>
    /// Reads the recovery dossier from the client: the id_token's claims plus the League client's
    /// email. Enrichment, so a failure here never turns a successful sign-in into a failed one.
    /// </summary>
    private async Task ReadAccountFactsAsync(LoginContext context, CancellationToken cancellationToken)
    {
        if (_accountProbe is null) return;

        try
        {
            var facts = await _accountProbe.TryReadAsync(cancellationToken);
            if (facts is null)
            {
                _trace.Write("no account details available from the client");
                return;
            }

            context.AccountFactsObserved?.Invoke(facts);

            // Presence only, never the values — this file is not encrypted.
            _trace.Write("account details read from the client: "
                         + (facts.MaskedEmail is not null ? "email " : "")
                         + (facts.MfaEnabled ? "mfa " : "")
                         + (facts.PhoneOnFile == true ? "phone " : "")
                         + (facts.CountrySetUtc is not null ? "country-date " : "")
                         + facts.Regions.Count + " region(s)");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _trace.Write("could not read account details: " + ex.GetType().Name);
        }
    }

    /// <summary>
    /// Reads rank, wallet and collection from the client — the same data the Riot API provides, but
    /// with no key, no rate limit and no expiry. Enrichment, so failure never fails the sign-in.
    /// </summary>
    private async Task ReadClientStatsAsync(LoginContext context, CancellationToken cancellationToken)
    {
        if (_statsProbe is null) return;

        try
        {
            var stats = await _statsProbe.TryReadAsync(cancellationToken);
            if (stats is null)
            {
                _trace.Write("no stats available from the client");
                return;
            }

            context.ClientStatsObserved?.Invoke(stats);
            _trace.Write("client stats: solo=" + (stats.Solo?.ToString() ?? "-")
                         + " flex=" + (stats.Flex?.ToString() ?? "-")
                         + " champs=" + (stats.ChampionsOwned?.ToString() ?? "-")
                         + " skins=" + (stats.SkinsOwned?.ToString() ?? "-")
                         + " honor=" + (stats.HonorLevel?.ToString() ?? "-"));

            if (!stats.CollectionIsComplete)
                _trace.Write("the client had not finished loading its inventory; "
                             + "keeping the previously stored collection rather than overwriting it");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _trace.Write("could not read client stats: " + ex.GetType().Name);
        }
    }

    /// <summary>
    /// Reads the loot inventory. Enrichment like the rest, so a failure never fails the sign-in.
    /// </summary>
    private ClientMatchProbe? _matchProbe;

    /// <summary>
    /// Reads recent ranked results.
    ///
    /// Last in the pass on purpose: match history is served late by the client, and unlike the
    /// collection it is a nice-to-have, so it must never delay anything that matters.
    /// </summary>
    private async Task ReadMatchesAsync(LoginContext context, CancellationToken cancellationToken)
    {
        if (_matchProbe is null) return;

        try
        {
            var matches = await _matchProbe.TryReadAsync(cancellationToken);
            if (matches is null)
            {
                _trace.Write("no match history available from the client");
                return;
            }

            context.MatchesObserved?.Invoke(matches);
            _trace.Write("matches: " + matches.Recent.Count + " ranked results, "
                         + matches.Wins + "W " + matches.Losses + "L");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never fatal: a missing form chart must not fail a sign-in that otherwise worked.
            _trace.Write("match history unavailable (" + ex.GetType().Name + ")");
        }
    }

    private async Task ReadLootAsync(LoginContext context, CancellationToken cancellationToken)
    {
        if (_lootProbe is null) return;

        try
        {
            var loot = await _lootProbe.TryReadAsync(cancellationToken);
            if (loot is null || loot.IsEmpty)
            {
                _trace.Write("no loot available from the client");
                return;
            }

            context.LootObserved?.Invoke(loot);
            _trace.Write("loot: " + loot.Items.Count + " entries across "
                         + loot.Grouped().Count + " categories");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _trace.Write("could not read loot: " + ex.GetType().Name);
        }
    }

    /// <summary>
    /// Puts your saved League settings back after the switch.
    ///
    /// Runs last, and declines while the client is up: League holds these files open and rewrites
    /// them on exit, so restoring underneath it would look like it worked and then silently revert.
    /// </summary>
    private void RestoreGameSettings(LoginContext context)
    {
        if (_settings is null || !context.Settings.CarryGameSettings) return;

        var clientRunning = System.Diagnostics.Process.GetProcessesByName("LeagueClient").Length > 0;
        var result = _settings.Restore(clientRunning);

        _trace.Write("game settings: " + result.Message);
    }
}
