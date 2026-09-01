using LAM.Core.Riot;
using LAM.Core.Windows;

// lam-diag — the pre-flight check for this machine.
//
// Its main job is to answer the one question the design could not settle by reading files:
// does this Riot Client version actually persist a resumable session when "Stay signed in" is
// ticked? If it does, logins after the first need no typing at all. If it does not, the app falls
// back to typing every time and nothing else changes.
//
//   lam-diag paths      where everything lives, and whether we found it
//   lam-diag session    what the client is storing right now
//   lam-diag roundtrip  prove we can rewrite RiotClientSettings.yaml without damaging it
//   lam-diag watch      the spike: watch the session file across a real login

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var paths = RiotPaths.Discover();

return command switch
{
    "paths" => ShowPaths(),
    "session" => ShowSession(),
    "roundtrip" => RoundTrip(),
    "watch" => await WatchAsync(),
    "collection" => await ShowCollectionAsync(),
    _ => Help(),
};

int Help()
{
    Console.WriteLine("""
        lam-diag — League Account Manager pre-flight checks

          paths       Show the discovered Riot install and config locations.
          session     Show what the Riot Client currently has stored for login.
          roundtrip   Rewrite RiotClientSettings.yaml in memory and verify nothing is lost.
          watch       Watch the session file while you log in, and report what changed.
          collection  Read rank, collection, loot and the recovery dossier from the running
                      client, using the same code the app does. Needs League signed in.

        Start with:  lam-diag session
        """);
    return 0;
}

int ShowPaths()
{
    Console.WriteLine("Riot Client");
    Console.WriteLine("  manifest        " + Describe(RiotPaths.InstallsManifest));
    Console.WriteLine("  RiotClientSvcs  " + Describe(paths.ClientServicesExe));
    Console.WriteLine("  League install  " + (paths.LeagueInstallDirectory ?? "(not registered)"));
    Console.WriteLine();
    Console.WriteLine("Files we read or write");
    Console.WriteLine("  settings        " + Describe(paths.ClientSettingsFile));
    Console.WriteLine("  session         " + Describe(paths.PrivateSettingsFile));
    Console.WriteLine("  client lockfile " + Describe(paths.RiotClientLockfile));
    Console.WriteLine("  league lockfile " + Describe(paths.LeagueLockfile));
    Console.WriteLine();

    var processes = new RiotProcessManager();
    Console.WriteLine("Processes");
    Console.WriteLine("  game in progress  " + (processes.IsGameInProgress() ? "YES — a switch would be refused" : "no"));
    var running = processes.DescribeRunningClients();
    Console.WriteLine("  client processes  " + (running.Count == 0 ? "none" : string.Join(", ", running)));
    Console.WriteLine("  never touched     " + string.Join(", ", RiotProcessManager.ProtectedProcesses) + " (Vanguard)");
    Console.WriteLine();

    // An integrity mismatch is what turns a working sign-in into three unrelated-looking errors,
    // so it is reported here rather than left to be discovered.
    Console.WriteLine("Elevation");
    Console.WriteLine("  running elevated  " + (ElevationInfo.IsElevated ? "yes" : "no"));

    var layers = ElevationInfo.RiotCompatibilityLayers();
    if (layers.Count == 0)
    {
        Console.WriteLine("  compat flags      (none)");
    }
    else
    {
        Console.WriteLine("  compat flags");
        foreach (var layer in layers) Console.WriteLine("    " + layer);
    }

    Console.WriteLine();
    Console.WriteLine("  " + ElevationInfo.DescribeCompatibility());

    // lam-diag is a plain console tool and deliberately runs unelevated, so the line above
    // describes THIS process. Without saying so, a MISMATCH here reads as a problem with the
    // account manager, which ships a requireAdministrator manifest and does run elevated.
    if (!ElevationInfo.IsElevated)
    {
        Console.WriteLine();
        Console.WriteLine("  (That describes lam-diag itself, which runs unelevated on purpose.");
        Console.WriteLine("   The account manager requests administrator on launch, so it matches");
        Console.WriteLine("   the client. Check Settings > Diagnostics to confirm.)");
    }

    var yaml = new RiotYamlService(paths, Path.GetTempPath());
    var (region, locale) = yaml.ReadRegionAndLocale();
    Console.WriteLine();
    Console.WriteLine("Current region/locale: " + (region ?? "?") + " / " + (locale ?? "?"));
    return 0;
}

int ShowSession()
{
    var yaml = new RiotYamlService(paths, Path.GetTempPath());
    var text = yaml.ReadPrivateSettings();

    if (text is null)
    {
        Console.WriteLine("No RiotGamesPrivateSettings.yaml found at:");
        Console.WriteLine("  " + paths.PrivateSettingsFile);
        Console.WriteLine();
        Console.WriteLine("Launch the Riot Client once, then run this again.");
        return 1;
    }

    var cookies = RiotYamlService.DescribeCookies(text);
    var hasSession = RiotYamlService.ContainsSession(text);

    Console.WriteLine("Session file: " + paths.PrivateSettingsFile);
    Console.WriteLine("  size            " + new FileInfo(paths.PrivateSettingsFile).Length + " bytes");
    Console.WriteLine("  cookies stored  " + (cookies.Count == 0 ? "(none)" : string.Join(", ", cookies)));
    Console.WriteLine("  resumable       " + (hasSession ? "YES" : "no"));
    Console.WriteLine();

    if (hasSession)
    {
        Console.WriteLine("This client DOES persist a resumable session.");
        Console.WriteLine("Session swapping will work: after the first login, switching accounts");
        Console.WriteLine("needs no typing at all.");
    }
    else
    {
        Console.WriteLine("Only a device cookie is stored, so there is nothing to resume.");
        Console.WriteLine("That is expected when \"Stay signed in\" has never been ticked.");
        Console.WriteLine();
        Console.WriteLine("To find out whether this client can persist a session, run:");
        Console.WriteLine("    lam-diag watch");
        Console.WriteLine("and log in with \"Stay signed in\" ticked while it watches.");
    }

    return 0;
}

int RoundTrip()
{
    var path = paths.ClientSettingsFile;
    if (!File.Exists(path))
    {
        Console.WriteLine("RiotClientSettings.yaml not found at " + path);
        return 1;
    }

    var original = RiotYamlService.LoadMapping(path);
    if (original is null)
    {
        Console.WriteLine("Could not parse RiotClientSettings.yaml — the app will leave region alone.");
        return 1;
    }

    var before = CountKeys(original);
    var rewritten = RiotYamlService.Serialise(original);
    var reparsed = RiotYamlService.ParseMapping(rewritten);

    if (reparsed is null)
    {
        Console.WriteLine("FAIL: the rewritten document does not parse. Region writing must stay off.");
        return 2;
    }

    var after = CountKeys(reparsed);
    Console.WriteLine("RiotClientSettings.yaml round-trip (in memory — your file is untouched)");
    Console.WriteLine("  keys before  " + before);
    Console.WriteLine("  keys after   " + after);
    Console.WriteLine("  bytes before " + new FileInfo(path).Length);
    Console.WriteLine("  bytes after  " + System.Text.Encoding.UTF8.GetByteCount(rewritten));

    var scratch = Path.Combine(Path.GetTempPath(), "lam-roundtrip.yaml");
    File.WriteAllText(scratch, rewritten);
    Console.WriteLine("  written to   " + scratch + "  (compare it yourself if you like)");
    Console.WriteLine();
    Console.WriteLine(before == after
        ? "OK — every key survived. Safe to write region/locale."
        : "FAIL — key count changed. Region writing must stay off.");

    return before == after ? 0 : 2;
}

async Task<int> WatchAsync()
{
    var file = paths.PrivateSettingsFile;
    var yaml = new RiotYamlService(paths, Path.GetTempPath());

    Console.WriteLine("Watching " + file);
    Console.WriteLine();
    Console.WriteLine("  1. Log out of the Riot Client if you are signed in.");
    Console.WriteLine("  2. Log in again, and TICK \"Stay signed in\".");
    Console.WriteLine("  3. Watch the lines below. Ctrl+C when you are done.");
    Console.WriteLine();

    var previous = Summarise(yaml.ReadPrivateSettings());
    Console.WriteLine("[start] " + previous);

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

    try
    {
        while (!cancellation.IsCancellationRequested)
        {
            await Task.Delay(1000, cancellation.Token);

            var current = Summarise(yaml.ReadPrivateSettings());
            if (current == previous) continue;

            Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + current);
            previous = current;

            if (RiotYamlService.ContainsSession(yaml.ReadPrivateSettings()))
            {
                Console.WriteLine();
                Console.WriteLine("=========================================================");
                Console.WriteLine(" A resumable session appeared. Session swapping WORKS on");
                Console.WriteLine(" this client, so the app will type your password once per");
                Console.WriteLine(" account and never again.");
                Console.WriteLine("=========================================================");
                return 0;
            }
        }
    }
    catch (OperationCanceledException)
    {
        // Ctrl+C — fall through to the summary below.
    }

    Console.WriteLine();
    Console.WriteLine("Stopped without seeing a resumable session.");
    Console.WriteLine("If you did tick \"Stay signed in\", this client keeps its session somewhere");
    Console.WriteLine("else, and the app will type your password on every switch instead.");
    return 1;
}

static string Summarise(string? text)
{
    if (text is null) return "file missing";
    var cookies = RiotYamlService.DescribeCookies(text);
    var session = RiotYamlService.ContainsSession(text) ? "RESUMABLE" : "device-only";
    return session + " | cookies: " + (cookies.Count == 0 ? "(none)" : string.Join(",", cookies))
                   + " | " + text.Length + " chars";
}

static string Describe(string? path)
{
    if (string.IsNullOrEmpty(path)) return "(unknown)";
    var exists = File.Exists(path) || Directory.Exists(path);
    return path + (exists ? "  [found]" : "  [MISSING]");
}

static int CountKeys(YamlDotNet.RepresentationModel.YamlMappingNode node)
{
    var total = 0;

    void Walk(YamlDotNet.RepresentationModel.YamlNode current)
    {
        switch (current)
        {
            case YamlDotNet.RepresentationModel.YamlMappingNode mapping:
                foreach (var pair in mapping.Children)
                {
                    total++;
                    Walk(pair.Value);
                }
                break;
            case YamlDotNet.RepresentationModel.YamlSequenceNode sequence:
                foreach (var child in sequence.Children) Walk(child);
                break;
        }
    }

    Walk(node);
    return total;
}

/// <summary>
/// Reads everything the app reads, through the app's own code, against the live client.
///
/// This exists because the numbers here were wrong for a long time in ways unit tests could not
/// catch: the champion count was the free rotation, and every skin resolved to a champion that did
/// not exist. Both looked perfectly healthy on screen. Printing the real figures beside the client's
/// own is the only check that would have failed.
/// </summary>
async Task<int> ShowCollectionAsync()
{
    var cancellation = CancellationToken.None;

    Console.WriteLine("Recovery dossier");
    var facts = await new ClientAccountProbe(paths, new RiotYamlService(paths, Path.GetTempPath()))
        .TryReadAsync(cancellation);

    if (facts is null)
    {
        Console.WriteLine("  (nothing — is the Riot Client running and signed in?)");
    }
    else
    {
        Console.WriteLine("  riot id           " + facts.GameName + "#" + facts.TagLine);
        Console.WriteLine("  created           " + Date(facts.CreatedUtc) + "   <- the real one");
        Console.WriteLine("  country set       " + Date(facts.CountrySetUtc) + "   <- what this used to show");
        Console.WriteLine("  password changed  " + Date(facts.PasswordChangedUtc));
        Console.WriteLine("  legacy username   " + (facts.LegacyUsername ?? "-"));
        Console.WriteLine("  original region   " + (facts.OriginalPlatform ?? "-"));
        Console.WriteLine("  legacy account id " + (facts.LegacyAccountId ?? "-"));
        Console.WriteLine("  masked email      " + (facts.MaskedEmail ?? "-"));
        Console.WriteLine("  phone             " + (facts.MaskedPhone ?? "-"));
        Console.WriteLine("  riot id history   " + (facts.Aliases.Count == 0
            ? "-"
            : string.Join(" | ", facts.Aliases.Select(a => a.ToString()))));
        Console.WriteLine("  regions seen      " + (facts.Regions.Count == 0
            ? "-"
            : string.Join(", ", facts.Regions)));
    }

    Console.WriteLine();
    Console.WriteLine("Collection (waits for the client's inventory to finish loading)");
    var stats = await new ClientStatsProbe(paths).TryReadAsync(cancellation);

    if (stats is null)
    {
        Console.WriteLine("  (nothing — is League running?)");
    }
    else
    {
        Console.WriteLine("  complete          " + (stats.CollectionIsComplete
            ? "yes"
            : "NO — this capture would be refused rather than stored"));
        Console.WriteLine("  champions owned   " + (stats.ChampionsOwned?.ToString() ?? "-")
                          + "   (of " + stats.OwnedChampionIds.Length + " entries returned)");
        Console.WriteLine("  skins owned       " + (stats.SkinsOwned?.ToString() ?? "-"));
        Console.WriteLine("  first champion    " + (stats.FirstChampionId?.ToString() ?? "-")
                          + "  bought " + Date(stats.FirstChampionPurchasedUtc));
        Console.WriteLine("  solo / flex       " + (stats.Solo?.ToString() ?? "-") + "  |  " + (stats.Flex?.ToString() ?? "-"));
        Console.WriteLine("  last season ended " + (stats.PreviousSeasonPeak ?? "-"));
        Console.WriteLine("  mastery           " + (stats.Mastery is null
            ? "-"
            : stats.Mastery.TotalPoints.ToString("N0") + " pts over " + stats.Mastery.ChampionsPlayed + " champions"));
        Console.WriteLine("  season rewards    " + (stats.SeasonRewards.Count == 0
            ? "-"
            : string.Join(", ", stats.SeasonRewards.Select(r => "S" + r.SeasonId))));
        Console.WriteLine("  acquisition dates " + stats.SkinAcquiredEpochSeconds.Count + " skins dated");
        Console.WriteLine("  RP / BE           " + (stats.RiotPoints?.ToString("N0") ?? "-")
                          + " / " + (stats.BlueEssence?.ToString("N0") ?? "-"));
    }

    Console.WriteLine();
    Console.WriteLine("Skin catalogue");
    var catalogue = new SkinCatalogue(Path.Combine(Path.GetTempPath(), "lam-diag-catalogue"));
    if (!await catalogue.RefreshAsync(paths, TimeSpan.Zero, cancellation))
    {
        Console.WriteLine("  (could not capture — is League running?)");
    }
    else
    {
        Console.WriteLine("  skins known       " + catalogue.SkinCount);

        var sample = catalogue.Resolve((stats?.OwnedSkinIds ?? []).Take(6).ToArray());
        var unknown = catalogue.Resolve(stats?.OwnedSkinIds ?? [])
            .Count(s => catalogue.ChampionName(s.ChampionId) == "Unknown champion");

        Console.WriteLine("  unresolved names  " + unknown + "   <- must be 0");

        // Prices and availability, which drive the fleet view's RP totals and legacy detection.
        var owned = (stats?.OwnedSkinIds ?? []).Select(catalogue.Skin).Where(s => s is not null).ToList();
        var pricedRp = owned.Where(s => s!.RpCost is > 0).Sum(s => (long)s!.RpCost!.Value);
        Console.WriteLine("  priced skins      " + owned.Count(s => s!.RpCost is > 0)
                          + " of " + owned.Count + "   totalling " + pricedRp.ToString("N0") + " RP");
        Console.WriteLine("  no longer sold    " + owned.Count(s => s!.IsLegacy));
        foreach (var skin in sample)
            Console.WriteLine("     " + skin.Name.PadRight(30) + catalogue.ChampionName(skin.ChampionId));
    }

    Console.WriteLine();
    Console.WriteLine("Loot");
    var loot = await new ClientLootProbe(paths).TryReadAsync(cancellation);

    if (loot is null || loot.IsEmpty)
    {
        Console.WriteLine("  (nothing)");
    }
    else
    {
        foreach (var group in loot.Grouped())
        {
            Console.WriteLine("  " + group.Title);
            foreach (var item in group.Items)
                Console.WriteLine("     " + item.Name.PadRight(30)
                                  + (item.IsCurrency ? item.Count.ToString("N0") : "x" + item.Count)
                                  + (item.IsExpiring ? "   (expires)" : ""));
        }
    }

    return 0;
}

static string Date(DateTimeOffset? value) => value?.LocalDateTime.ToString("d MMM yyyy") ?? "-";
