using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using LAM.Core.Model;

namespace LAM.Core.Riot;

/// <summary>
/// What the signed-in client already knows about the account, read locally.
///
/// Riot's public API exposes no email, no creation date and no security settings — which is true, and
/// was the wrong reason to leave the whole recovery dossier to be typed by hand. The *client* holds
/// far more: the id_token it stores decodes to a set of claims covering most of what a support
/// ticket asks for, and the League client will report the registered email.
///
/// Everything here is local. No API key, no network beyond loopback, nothing sent anywhere.
/// </summary>
public sealed class ClientAccountProbe
{
    private readonly RiotPaths _paths;
    private readonly RiotYamlService _yaml;

    public ClientAccountProbe(RiotPaths paths, RiotYamlService yaml)
    {
        _paths = paths;
        _yaml = yaml;
    }

    /// <summary>Reads everything available, tolerating each source being absent.</summary>
    public async Task<ClientAccountFacts?> TryReadAsync(CancellationToken cancellationToken)
    {
        // Both sources, merged rather than one or the other. The live endpoint carries claims the
        // stored id_token lacks — the real creation date, the legacy username, the original region —
        // but the token carries the region *history* (`lol_region`) that userinfo does not, and that
        // history is what shows an account has been transferred. Preferring one would drop the other.
        var stored = ReadIdTokenClaims(_yaml.ReadPrivateSettings());
        var live = await TryReadUserInfoAsync(cancellationToken);

        var facts = live is null
            ? stored ?? new ClientAccountFacts()
            : live.MergedWith(stored);

        var aliases = await TryReadAliasesAsync(cancellationToken);
        if (aliases.Count > 0) facts = facts with { Aliases = aliases };

        if (await TryReadPhoneAsync(cancellationToken) is { } phone)
            facts = facts with { PhoneCountryCode = phone.CountryCode, PhoneEndsWith = phone.EndsWith };

        var email = await TryReadEmailAsync(cancellationToken);
        if (email is not null)
        {
            // Only overwrite when the client actually answered. This used to coalesce a missing
            // property to false, which then wrote over the id_token's account_verified claim — so a
            // renamed field would silently report a verified account as unverified.
            facts = facts with
            {
                MaskedEmail = email.Value.Email,
                EmailVerified = email.Value.Verified ?? facts.EmailVerified,
            };
        }

        return facts.IsEmpty ? null : facts;
    }

    // ---- Riot Client userinfo ----------------------------------------------

    internal const string UserInfoPath = "/rso-auth/v1/authorization/userinfo";

    /// <summary>
    /// Asks the Riot Client what it knows about the signed-in account.
    ///
    /// This is the richest local source by a distance, and it is what makes the recovery sheet worth
    /// having: it carries the genuine creation date, the last password change, the legacy login
    /// username and the region the account was originally made on — the exact questions Riot Support
    /// asks. The stored id_token has none of them.
    /// </summary>
    private async Task<ClientAccountFacts?> TryReadUserInfoAsync(CancellationToken cancellationToken)
    {
        var lockfile = Lockfile.Read(_paths.RiotClientLockfile);
        if (lockfile is null) return null;

        using var client = new RiotLocalApiClient(lockfile);
        using var document = await client.GetJsonAsync(UserInfoPath, cancellationToken);
        if (document is null) return null;

        return ParseUserInfo(document.RootElement);
    }

    /// <summary>
    /// Parses the userinfo response.
    ///
    /// Note the shape: the payload arrives as <c>{"userInfo": "…"}</c> where the value is a JSON
    /// *string* that has to be parsed a second time. It is not a JWT, despite sitting where one would.
    /// </summary>
    internal static ClientAccountFacts? ParseUserInfo(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        var inner = ReadString(root, "userInfo");
        JsonDocument? parsed = null;

        try
        {
            if (inner is not null)
            {
                try { parsed = JsonDocument.Parse(inner); }
                catch (JsonException) { return null; }
            }

            var claims = parsed?.RootElement ?? root;
            if (claims.ValueKind != JsonValueKind.Object) return null;

            return FromClaims(claims);
        }
        finally
        {
            parsed?.Dispose();
        }
    }

    internal const string AliasesPath = "/player-account/aliases/v1/aliases";

    /// <summary>
    /// Every Riot ID this account has used, according to Riot.
    ///
    /// Better than the rename history this app keeps: that only records names seen while the app was
    /// watching, whereas this is Riot own record with the date each was taken. A recovery form asks
    /// for previous names by title.
    /// </summary>
    internal static IReadOnlyList<AccountAlias> ParseAliases(JsonElement root)
    {
        var array = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when root.TryGetProperty("aliases", out var nested)
                                      && nested.ValueKind == JsonValueKind.Array => nested,
            _ => default,
        };

        if (array.ValueKind != JsonValueKind.Array) return [];

        var aliases = new List<AccountAlias>();

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;

            var name = ReadString(entry, "game_name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            aliases.Add(new AccountAlias(
                name,
                ReadString(entry, "tag_line"),
                ReadEpochMilliseconds(entry, "created_datetime"),
                ReadBool(entry, "active") ?? false));
        }

        return [.. aliases.OrderBy(a => a.CreatedUtc ?? DateTimeOffset.MaxValue)];
    }

    private async Task<IReadOnlyList<AccountAlias>> TryReadAliasesAsync(CancellationToken cancellationToken)
    {
        var lockfile = Lockfile.Read(_paths.RiotClientLockfile);
        if (lockfile is null) return [];

        using var client = new RiotLocalApiClient(lockfile);
        using var document = await client.GetJsonAsync(AliasesPath, cancellationToken);

        return document is null ? [] : ParseAliases(document.RootElement);
    }

    internal const string PhonePath = "/lol-account-verification/v1/phone-number";

    /// <summary>
    /// The masked phone number on the account: country code and last four digits.
    ///
    /// An upgrade on the yes/no this used to store - enough to check against a number you remember
    /// without the client ever revealing the whole thing.
    /// </summary>
    internal static (string? CountryCode, string? EndsWith)? ParsePhone(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        if (!data.TryGetProperty("phoneNumberObfuscated", out var phone) || phone.ValueKind != JsonValueKind.Object)
            return null;

        var code = ReadString(phone, "countryCode");
        var ends = ReadString(phone, "endsWith");

        return code is null && ends is null ? null : (code, ends);
    }

    private async Task<(string? CountryCode, string? EndsWith)?> TryReadPhoneAsync(CancellationToken cancellationToken)
    {
        var lockfile = Lockfile.Read(_paths.LeagueLockfile);
        if (lockfile is null) return null;

        using var client = new RiotLocalApiClient(lockfile);
        using var document = await client.GetJsonAsync(PhonePath, cancellationToken);

        return document is null ? null : ParsePhone(document.RootElement);
    }

    // ---- id_token ----------------------------------------------------------

    /// <summary>
    /// Decodes the claims from the client's stored id_token.
    ///
    /// The signature is deliberately not checked. This reads a token the client obtained, holds and
    /// already trusts, purely to show it back to its owner — nothing is authorised on the strength
    /// of it, so verification would be ceremony rather than security.
    /// </summary>
    public static ClientAccountFacts? ReadIdTokenClaims(string? privateSettingsYaml)
    {
        var root = privateSettingsYaml is null ? null : RiotYamlService.ParseMapping(privateSettingsYaml);
        if (root is null) return null;

        var token = RiotYamlService.GetScalar(root, "psl", "authorization", "riot-client", "id_token");
        if (string.IsNullOrWhiteSpace(token)) return null;

        var payload = DecodePayload(token);
        if (payload is null) return null;

        using (payload)
        {
            var claims = payload.RootElement;
            if (claims.ValueKind != JsonValueKind.Object) return null;

            return FromClaims(claims);
        }
    }

    /// <summary>
    /// Builds the dossier from a claim set, whichever source it came from.
    ///
    /// The id_token and the live userinfo response overlap heavily but not perfectly — <c>lol</c> is
    /// an array in one and an object in the other, and each carries fields the other omits — so the
    /// readers below tolerate both shapes and simply leave absent fields null.
    /// </summary>
    private static ClientAccountFacts FromClaims(JsonElement claims) => new()
    {
        RiotAccountId = ReadString(claims, "sub"),
        GameName = ReadNested(claims, "acct", "game_name"),
        TagLine = ReadNested(claims, "acct", "tag_line"),
        AccountState = ReadNested(claims, "acct", "state"),
        EmailVerified = ReadBool(claims, "account_verified"),
        PhoneOnFile = ReadBool(claims, "phone_number_verified"),
        MfaEnabled = ReadAuthMethods(claims).Contains("mfa", StringComparer.OrdinalIgnoreCase),
        Country = ReadString(claims, "country"),
        CountrySetUtc = ReadEpochMilliseconds(claims, "country_at"),
        Regions = ReadRegions(claims),
        SummonerUid = ReadNestedNumber(claims, "lol", "uid"),
        ClientUsername = ReadNestedString(claims, "lol", "uname"),

        // The recovery material proper.
        CreatedUtc = ReadNestedEpoch(claims, "acct", "created_at"),
        PasswordChangedUtc = ReadNestedEpoch(claims, "pw", "cng_at"),
        MustResetPassword = ReadNestedBool(claims, "pw", "must_reset"),
        LegacyUsername = ReadString(claims, "username") ?? ReadString(claims, "preferred_username"),
        OriginalPlatform = ReadString(claims, "original_platform_id"),
        LegacyAccountId = ReadNumberAsString(claims, "original_account_id"),
        PvpnetAccountId = ReadNumberAsString(claims, "pvpnet_account_id"),
        AgeCategory = ReadString(claims, "age_category"),
        EmailOnFile = ReadBool(claims, "email_set"),
        SummonerName = ReadNested(claims, "lol_account", "summoner_name"),
    };

    private static JsonDocument? DecodePayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            var segment = parts[1].Replace('-', '+').Replace('_', '/');
            segment = segment.PadRight(segment.Length + (4 - segment.Length % 4) % 4, '=');
            return JsonDocument.Parse(Convert.FromBase64String(segment));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    // ---- League client -----------------------------------------------------

    /// <summary>
    /// Asks the League client for the registered email.
    ///
    /// Riot masks it (<c>ma***@*****.com</c>), which is still worth having: it confirms *which*
    /// mailbox an account belongs to, and it can be checked against whatever address you typed.
    /// </summary>
    private async Task<(string Email, bool? Verified)?> TryReadEmailAsync(CancellationToken cancellationToken)
    {
        var lockfile = Lockfile.Read(_paths.LeagueLockfile);
        if (lockfile is null) return null;

        using var client = new RiotLocalApiClient(lockfile);
        using var document = await client.GetJsonAsync("/lol-email-verification/v1/email", cancellationToken);
        if (document is null) return null;

        var email = ReadString(document.RootElement, "email");
        if (string.IsNullOrWhiteSpace(email)) return null;

        return (email, ReadBool(document.RootElement, "emailVerified"));
    }

    // ---- claim readers -----------------------------------------------------

    /// <summary>
    /// A string claim, treating empty as absent.
    ///
    /// The distinction matters because two sources are merged: an empty <c>""</c> from the live
    /// response is not null, so it would win over a real value from the stored token and render as a
    /// blank field. Riot genuinely returns empty strings — <c>lol_account.summoner_name</c> has been
    /// one ever since Riot IDs replaced summoner names.
    /// </summary>
    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
           && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static bool? ReadBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string? ReadNested(JsonElement root, string parent, string child)
        => root.TryGetProperty(parent, out var node) && node.ValueKind == JsonValueKind.Object
            ? ReadString(node, child)
            : null;

    private static IReadOnlyList<string> ReadAuthMethods(JsonElement root)
    {
        if (!root.TryGetProperty("amr", out var amr) || amr.ValueKind != JsonValueKind.Array) return [];

        return amr.EnumerateArray()
            .Where(entry => entry.ValueKind == JsonValueKind.String)
            .Select(entry => entry.GetString()!)
            .ToList();
    }

    private static DateTimeOffset? ReadEpochMilliseconds(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        if (!value.TryGetInt64(out var epoch) || epoch <= 0) return null;

        return epoch < 100_000_000_000L
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : DateTimeOffset.FromUnixTimeMilliseconds(epoch);
    }

    /// <summary>
    /// Every region the account has a presence on, active first.
    ///
    /// Genuinely useful for recovery: an account showing an inactive NA entry alongside an active BR
    /// one has been transferred, and that history is the sort of detail a support ticket turns on.
    /// </summary>
    private static IReadOnlyList<AccountRegion> ReadRegions(JsonElement root)
    {
        if (!root.TryGetProperty("lol_region", out var regions) || regions.ValueKind != JsonValueKind.Array)
            return [];

        return regions.EnumerateArray()
            .Where(entry => entry.ValueKind == JsonValueKind.Object)
            .Select(entry => new AccountRegion(
                ReadString(entry, "cpid") ?? "?",
                ReadBool(entry, "active") ?? false))
            .Where(region => region.Platform != "?")
            .OrderByDescending(region => region.Active)
            .ToList();
    }

    private static string? ReadNestedString(JsonElement root, string arrayName, string field)
        => FirstEntry(root, arrayName) is { } entry ? ReadString(entry, field) : null;

    private static string? ReadNestedNumber(JsonElement root, string arrayName, string field)
    {
        if (FirstEntry(root, arrayName) is not { } entry) return null;
        return entry.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetRawText()
            : null;
    }

    /// <summary>
    /// The first object under a name, whether it holds an array of them or is one itself.
    ///
    /// The id_token puts <c>lol</c> in an array; the live userinfo response makes it a plain object.
    /// Handling both here keeps one set of field readers working against either source.
    /// </summary>
    private static JsonElement? FirstEntry(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var node)) return null;

        if (node.ValueKind == JsonValueKind.Object) return node;
        if (node.ValueKind != JsonValueKind.Array) return null;

        foreach (var entry in node.EnumerateArray())
            if (entry.ValueKind == JsonValueKind.Object) return entry;

        return null;
    }

    private static bool? ReadNestedBool(JsonElement root, string parent, string child)
        => root.TryGetProperty(parent, out var node) && node.ValueKind == JsonValueKind.Object
            ? ReadBool(node, child)
            : null;

    private static DateTimeOffset? ReadNestedEpoch(JsonElement root, string parent, string child)
        => root.TryGetProperty(parent, out var node) && node.ValueKind == JsonValueKind.Object
            ? ReadEpochMilliseconds(node, child)
            : null;

    /// <summary>Legacy ids are large numbers; kept as text so nothing is lost to precision.</summary>
    private static string? ReadNumberAsString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null,
        };
    }
}

/// <summary>One Riot ID the account has used, and when it was taken.</summary>
public sealed record AccountAlias(string GameName, string? TagLine, DateTimeOffset? CreatedUtc, bool Active)
{
    public override string ToString()
    {
        var name = string.IsNullOrEmpty(TagLine) ? GameName : GameName + "#" + TagLine;
        var when = CreatedUtc is { } created ? "  (from " + created.UtcDateTime.ToString("d MMM yyyy") + ")" : "";
        return name + (Active ? "  [current]" : "") + when;
    }
}

public sealed record AccountRegion(string Platform, bool Active)
{
    public override string ToString() => Platform + (Active ? " (active)" : " (inactive)");
}

/// <summary>
/// What the client reported. Every field is an *observation* — the app records these alongside what
/// you typed and never over the top of it.
/// </summary>
public sealed record ClientAccountFacts
{
    public string? RiotAccountId { get; init; }
    public string? GameName { get; init; }
    public string? TagLine { get; init; }
    public string? AccountState { get; init; }

    /// <summary>Masked by Riot, e.g. <c>ma***@*****.com</c>.</summary>
    public string? MaskedEmail { get; init; }

    public bool? EmailVerified { get; init; }
    public bool? PhoneOnFile { get; init; }
    public bool MfaEnabled { get; init; }

    public string? Country { get; init; }

    /// <summary>
    /// When the account's country was set. Not a creation date, but usually close to one and far
    /// better than nothing — Riot Support asks for an approximate date.
    /// </summary>
    public DateTimeOffset? CountrySetUtc { get; init; }

    public IReadOnlyList<AccountRegion> Regions { get; init; } = [];
    public string? SummonerUid { get; init; }
    public string? ClientUsername { get; init; }

    /// <summary>
    /// When the account was actually created.
    ///
    /// The real thing, from <c>acct.created_at</c> — not to be confused with
    /// <see cref="CountrySetUtc"/>, which this app used to present as an approximate creation date and
    /// which can be off by years.
    /// </summary>
    public DateTimeOffset? CreatedUtc { get; init; }

    /// <summary>When the password was last changed. A standard Riot Support question.</summary>
    public DateTimeOffset? PasswordChangedUtc { get; init; }

    public bool? MustResetPassword { get; init; }

    /// <summary>
    /// The legacy login username, which is not the Riot ID and often not the email either. Recovery
    /// forms ask for it by name.
    /// </summary>
    public string? LegacyUsername { get; init; }

    /// <summary>The platform the account was created on, e.g. <c>BR1</c> — not necessarily today's.</summary>
    public string? OriginalPlatform { get; init; }

    public string? LegacyAccountId { get; init; }
    public string? PvpnetAccountId { get; init; }
    public string? AgeCategory { get; init; }
    public bool? EmailOnFile { get; init; }
    public string? SummonerName { get; init; }

    /// <summary>Every Riot ID this account has used, oldest first, as Riot records them.</summary>
    public IReadOnlyList<AccountAlias> Aliases { get; init; } = [];

    /// <summary>Dialling code of the phone on the account, e.g. "44".</summary>
    public string? PhoneCountryCode { get; init; }

    /// <summary>Last digits of the phone on the account, e.g. "0000".</summary>
    public string? PhoneEndsWith { get; init; }

    /// <summary>The phone as much as Riot will reveal it, or null if there is none on file.</summary>
    public string? MaskedPhone => PhoneEndsWith is null
        ? null
        : (PhoneCountryCode is null ? "" : "+" + PhoneCountryCode + " ") + "*** " + PhoneEndsWith;

    public bool IsEmpty =>
        RiotAccountId is null && MaskedEmail is null && Country is null && Regions.Count == 0;

    /// <summary>
    /// Fills anything missing here from an older or less complete reading.
    ///
    /// Used to combine the live userinfo response with the stored id_token: each knows things the
    /// other does not, and taking only one would quietly drop the difference.
    /// </summary>
    public ClientAccountFacts MergedWith(ClientAccountFacts? other)
    {
        if (other is null) return this;

        return this with
        {
            RiotAccountId = RiotAccountId ?? other.RiotAccountId,
            GameName = GameName ?? other.GameName,
            TagLine = TagLine ?? other.TagLine,
            AccountState = AccountState ?? other.AccountState,
            MaskedEmail = MaskedEmail ?? other.MaskedEmail,
            EmailVerified = EmailVerified ?? other.EmailVerified,
            PhoneOnFile = PhoneOnFile ?? other.PhoneOnFile,
            MfaEnabled = MfaEnabled || other.MfaEnabled,
            Country = Country ?? other.Country,
            CountrySetUtc = CountrySetUtc ?? other.CountrySetUtc,
            Regions = Regions.Count > 0 ? Regions : other.Regions,
            SummonerUid = SummonerUid ?? other.SummonerUid,
            ClientUsername = ClientUsername ?? other.ClientUsername,
            CreatedUtc = CreatedUtc ?? other.CreatedUtc,
            PasswordChangedUtc = PasswordChangedUtc ?? other.PasswordChangedUtc,
            MustResetPassword = MustResetPassword ?? other.MustResetPassword,
            LegacyUsername = LegacyUsername ?? other.LegacyUsername,
            OriginalPlatform = OriginalPlatform ?? other.OriginalPlatform,
            LegacyAccountId = LegacyAccountId ?? other.LegacyAccountId,
            PvpnetAccountId = PvpnetAccountId ?? other.PvpnetAccountId,
            AgeCategory = AgeCategory ?? other.AgeCategory,
            EmailOnFile = EmailOnFile ?? other.EmailOnFile,
            SummonerName = SummonerName ?? other.SummonerName,
            Aliases = Aliases.Count > 0 ? Aliases : other.Aliases,
            PhoneCountryCode = PhoneCountryCode ?? other.PhoneCountryCode,
            PhoneEndsWith = PhoneEndsWith ?? other.PhoneEndsWith,
        };
    }

    /// <summary>
    /// Whether a typed address is consistent with the masked one the client reports.
    ///
    /// Compares only what the mask actually reveals — the visible prefix and the suffix after the
    /// last dot. A recovery email saved against the wrong account is the exact failure this dossier
    /// exists to prevent, and it is otherwise invisible until the day it matters.
    /// </summary>
    public bool? EmailLooksConsistentWith(string? typedEmail)
    {
        if (string.IsNullOrWhiteSpace(typedEmail) || string.IsNullOrWhiteSpace(MaskedEmail)) return null;
        if (!MaskedEmail.Contains('*')) return string.Equals(typedEmail, MaskedEmail, StringComparison.OrdinalIgnoreCase);

        var prefix = new string(MaskedEmail.TakeWhile(c => c != '*').ToArray());
        if (prefix.Length > 0 && !typedEmail.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var maskedSuffix = MaskedEmail[(MaskedEmail.LastIndexOf('.') + 1)..];
        var typedSuffix = typedEmail[(typedEmail.LastIndexOf('.') + 1)..];

        return string.Equals(maskedSuffix, typedSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Folds observations into an account, never over the top of something typed.
    ///
    /// The rule matters: what you entered is a statement about the account, what the client says is a
    /// statement about the session. When they disagree the human one is more likely to be the one
    /// worth keeping, and the disagreement itself is worth surfacing rather than silently resolving.
    /// </summary>
    public void ApplyTo(AccountEntry account, DateTimeOffset nowUtc)
    {
        var identity = account.Identity;
        var recovery = account.Recovery;

        if (RiotAccountId is not null) identity.RiotAccountId = RiotAccountId;
        if (GameName is not null) identity.RecordName(GameName, TagLine, nowUtc);
        if (SummonerUid is not null) identity.AccountId ??= SummonerUid;

        // Merge forward rather than replace. A capture taken while the Riot Client is closed falls
        // back to the stored id_token, which carries no creation date, no password-change date, no
        // legacy username and no original region — so assigning it wholesale would silently destroy
        // the dossier this feature exists for. MergedWith only backfills what this reading lacks, so
        // anything freshly observed still wins.
        var observed = MergedWith(identity.Observed);

        identity.Observed = observed;
        identity.LastRefreshedUtc = nowUtc;

        // The real creation date wins. This used to take `country_at`, which is when the account's
        // country was last set — on a long-lived account that can be the better part of a decade out,
        // and a recovery ticket answered with a creation date years wrong is a ticket that fails.
        // The old value stays as a fallback only because it beats having nothing.
        //
        // Read from the merged view, not from `this`: a thin capture would otherwise decline to fill
        // a date it no longer knows, and then the repair below could not correct one either.
        if (recovery.ApproximateCreated is null && (observed.CreatedUtc ?? observed.CountrySetUtc) is { } created)
            recovery.ApproximateCreated = DateOnly.FromDateTime(created.UtcDateTime);
        else
            observed.RepairAutoFilledCreationDate(recovery);
    }

    /// <summary>
    /// Replaces a creation date that an earlier build auto-filled from the wrong claim.
    ///
    /// "Never overwrite what you typed" is the right rule, but it cannot tell a typed value from one
    /// this app wrote itself — and an earlier build wrote <c>country_at</c> into this field. That
    /// made a wrong date permanent precisely because the correct one could never replace it.
    ///
    /// The repair is narrow on purpose: it only acts when the stored date matches <c>country_at</c>
    /// exactly *and* the client reports a different real creation date. A date that matches only by
    /// coincidence is left alone, and anything genuinely typed does not match at all.
    /// </summary>
    private void RepairAutoFilledCreationDate(RecoveryInfo recovery)
    {
        if (CreatedUtc is not { } real || CountrySetUtc is not { } countrySet) return;
        if (recovery.ApproximateCreated is not { } stored) return;

        // Both renderings count as "we wrote this". The buggy build stored the date in the machine's
        // local zone; the fix stores UTC. Those differ by a day either side of midnight — this
        // account's country_at is 19 Jan UTC and 18 Jan in UTC-3 — so matching only one rendering
        // would leave the wrong date in place on exactly the vaults that need repairing.
        var wrongLocal = DateOnly.FromDateTime(countrySet.LocalDateTime);
        var wrongUtc = DateOnly.FromDateTime(countrySet.UtcDateTime);
        var right = DateOnly.FromDateTime(real.UtcDateTime);

        if ((stored == wrongLocal || stored == wrongUtc) && stored != right)
            recovery.ApproximateCreated = right;
    }
}
