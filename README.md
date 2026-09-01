# League Account Manager

A protected vault for your Riot accounts. Click a card, and it signs in.

Windows, C# / WPF on .NET 9. Everything stays on your machine — no server, no sync, no telemetry, and
no API key required for anything that matters.

---

## The idea: type once, then never again

There is no clean way to log into the Riot Client programmatically. The client is Electron, its login
runs inside a webview against `auth.riotgames.com`, and its local API only exposes
`GET /rso-auth/v1/authorization` — a read-only "am I signed in?" query. Nothing accepts credentials.
That is why every tool in this space types your password and warns you not to move your mouse.

This app does that too — **once per account**. Then it captures the session the client just created,
and every later sign-in restores that session instead:

```
first sign-in for an account          every sign-in after that
────────────────────────────          ────────────────────────────
close the Riot Client                 close the Riot Client
clear the stale session               write the saved session back
launch                                set the account's region
type username / Tab / password        launch
tick "Stay signed in"                 confirm the session was accepted
capture the resulting session
                                      no typing. no keystrokes at all.
```

If Riot expires a session, the fast path reports it, the app types once more, captures a fresh
session, and carries on. You should almost never see it type.

The two mechanisms need **opposite** settings of the client's "Stay signed in" box, so the app owns
that state rather than leaving it to you.

---

## What it knows about each account

All of this is read from the **local client**. No API key, no rate limit, no expiry — the trade is
that it can only be read while that account is signed in, so what you get is an explicit snapshot
from the last sign-in rather than a live figure, and the UI says so.

| | |
|---|---|
| Riot ID, account id, level, icon | after the first sign-in |
| Solo and flex rank, LP, W/L, placements, peak tier, last season's finish | ↑ |
| RP and Blue Essence | ↑ |
| Champions and skins owned, with the date each was acquired | ↑ |
| Loot — shards, essence, keys, chests, eternals, with disenchant values and expiry | ↑ |
| Champion mastery and ranked season rewards | ↑ |
| **Account created**, last password change, legacy login username, original region | ↑ |
| **Every Riot ID the account has used**, with the date each was taken — Riot's own record | ↑ |
| Registered email (masked by Riot), phone (country code + last 4), 2FA, region history | ↑ |
| First champion and first skin ever bought, with dates | derived from purchase dates |
| Recovery email in full, purchase receipts, 2FA backup codes, security answers | **typed by you** — nothing local exposes them |

The last row is short on purpose. The League client's entire RPC surface — all 1,465 functions — was
enumerated looking for purchase or receipt history. **There is none.** `/lol-inventory/v1/wallet/transactions`
looks like it would be and is a false positive. So the earliest-purchase reference stays a field you
fill in, and the app does not pretend otherwise.

**Recovery sheet** (card `⋯` menu) renders the lot as one page ordered the way a support ticket wants
it, and **omits secrets by default** so it is safe to paste.

---

## The fleet view

The part a single-account tool cannot do. Every companion app attaches to whichever client is
running, so it only ever sees one account; a vault holding all of them can answer questions that are
meaningless for one.

- **Find a skin across every account** — "who owns Elementalist Lux, and which account should I buy it
  on?" Accounts never signed into are left out of *both* columns, because an account that has not been
  read cannot say what it owns, and guessing would send you to buy something twice.
- **Portfolio** — totals, distinct skins owned, how much of it is no longer sold, and RP at listed
  prices. That figure is labelled *"at listed prices"* and never as a value: it is not what was paid,
  and it is emphatically not what an account is worth. Skins with no listed price are counted and
  reported as unpriced rather than estimated — inventing numbers would make the total look precise
  while being fiction.
- **Dormancy and ranked decay** — the silent failure of owning many accounts. It only touches the ones
  you are not looking at, and by the time you notice, the LP is gone.
- **Which sign-ins are still instant**, and which will need the password typed next time.
- **Export to CSV**, and a **repair toolbox** for a client that has got itself stuck: close the
  clients, clear a stale lockfile, clear the embedded browser's cache.

---

## Vault safety

- **Deleting is reversible.** *Move to trash* hides an account but keeps its password, session and
  recovery dossier; purging is a separate, deliberate act. The dossier is the one thing here that
  signing in again cannot rebuild.
- **Reused-password detection.** With a pile of smurfs, one leaked credential pair tried everywhere
  takes all of them together. Passwords are compared by a fingerprint held only in memory, so no list
  of them is ever assembled, and no finding quotes a secret.
- **Password history**, so a rotation typed with a typo is not a lockout.
- **Copying a password** excludes it from Windows clipboard history and Cloud Clipboard sync — else a
  password from an encrypted local vault ends up in plaintext somewhere the vault has no say over —
  and clears it afterwards, but only if it is still the thing on the clipboard.
- **Backups are checked**, including whether they sit on the same drive as the vault they protect. A
  rolling backup beside the file covers a bad write and nothing else: not a failed disk, not
  ransomware, not a wiped profile.
- **Streamer mode** hides account names automatically while capture software is running. Your own
  labels are left alone — it is the Riot ID and login name that are searchable.

---

## Safety

**A running game blocks everything.** Switching accounts closes the Riot Client, and doing that
mid-match earns a leaver penalty. If `League of Legends.exe` is running, the sign-in is refused
outright — before any process is touched.

**Typing stops if focus moves.** Synthetic keystrokes land wherever focus is *at the moment they are
delivered*, not where you aimed them. That is the real reason other tools tell you not to touch the
mouse: alt-tab halfway through and the rest of your password gets typed into Discord. This app pins
the target window before the first keystroke and re-checks it before every one after; the moment it
stops matching, typing stops and nothing further is sent.

**Vanguard is never touched.** `vgc`, `vgk` and `vgtray` are left strictly alone. The session-swap
path injects no input and reads no process memory at all, which is the other reason it is preferred.

**Refreshing an account refuses unless the client is signed into that same account.** The client can
only answer for whoever is signed in, so refreshing against the wrong one would copy that account's
rank, collection and recovery details onto this card — silently, permanently, and looking entirely
plausible afterwards.

**Loot is strictly read-only.** No disenchant, reroll or redeem. An automation bug there would destroy
something unrecoverable.

**Your client settings are not clobbered.** `RiotClientSettings.yaml` holds ~96 keys that are none of
our business — patch-note hashes, telemetry opt-outs, A/B cohorts, saved UI state. The app edits only
`install.globals.region/locale` and `install.localization.region/locale`, via a load-mutate-save on
YamlDotNet's representation model, and keeps a pristine copy of the original before its first write.

---

## Security

```
master password ──Argon2id(64 MiB, t=3, p=4)──► vault key (32 bytes)
                                                    │
                          AES-256-GCM ◄─────────────┤   accounts + recovery data
                                 │
                    DPAPI(CurrentUser) │  ← binds the file to this Windows profile
                                 │
   Windows Hello ─► hardware-backed signature ─► HKDF ─► unwraps a second copy of the key
```

- **A stolen `vault.dat` is useless on another machine** — without your profile's DPAPI key the body
  cannot even be reduced to ciphertext worth attacking.
- **Someone at your unlocked PC still needs the password or your face.** The reference tool this was
  modelled on uses only a hardware-derived key, which means anyone sitting at your desk can read
  every password. That is the gap this closes.
- **Windows Hello is a second door to the same key, never a replacement.** The master password always
  works, so a wiped Windows profile is an inconvenience rather than the permanent loss of everything.
  Hello enrolment signs a challenge twice and refuses if the signature is not reproducible, because a
  scheme that enrolled cleanly and then failed to unlock would fail at the worst possible moment.
- **Tamper-evident.** The header is fed to AES-GCM as associated data, so flipping one byte anywhere —
  including downgrading the recorded Argon2 cost — fails authentication instead of silently weakening
  the vault.
- **Auto-locks** on idle, on Windows locking, and optionally on minimise. The key is zeroed on lock.
- **Secrets never reach a log.** Passwords and backup codes are typed `SecretText`, whose `ToString()`
  returns `***`, and a redacting serialiser masks every one of them at once. Tests assert it, and that
  the CSV export contains no secret of any kind.
- Every save is atomic and takes a timestamped backup first, so a crash mid-write costs you the last
  save, never the vault.

---

## It asks for administrator, and why

The app requests elevation on launch. That is not enthusiasm for privilege — it is forced.

`RiotClientServices.exe` ships an `asInvoker` manifest, so in principle nothing here needs admin. But
Windows may carry a *"Run as administrator"* compatibility flag on it:

```
HKCU\...\AppCompatFlags\Layers
  RiotClientServices.exe       => ~ RUNASADMIN
  LeagueClient.exe             => ~ RUNASADMIN
  League of Legends.exe        => ~ RUNASADMIN
```

That makes the Riot Client run at high integrity, and Windows UIPI then forbids a normal-integrity
process from doing all three things this app needs: **typing into it**, **reading its window**, and
**closing it** — each failing with its own unrelated-looking error. Matching its level is the only way
to keep those working without altering your Riot setup.

If you clear those tick boxes (Properties → Compatibility on each executable), nothing needs elevation
any more and `requireAdministrator` can come back out of `src/LAM.App/app.manifest`.

**Settings → Diagnostics** reports your current integrity level and any Riot compatibility flags, so a
mismatch reads as a stated fact rather than a mystery. `lam-diag paths` prints the same thing.

---

## First run

1. Build it (see below), or run `LeagueAccountManager.exe`.
2. Choose a master password. **There is no reset.** Write it down somewhere physical.
3. Say yes to Windows Hello when offered — it becomes your everyday unlock.
4. **Add account** → name, Riot login username, password, region.
5. Click **Sign in** on the card. The first time it types; watch it, then leave it alone.
6. After that the app fills in everything in the table above by itself, and the button changes from
   *"Types the saved password"* to *"Sign in from the saved session — no typing"*.

Clicking the card body opens the account rather than signing in — a sign-in closes the Riot Client and
League, so it should never happen because a window was opened.

---

## lam-diag

A command-line pre-flight tool, so double-clicking it does nothing useful. Open a terminal in
`publish\diag\` (or `tools\LAM.Diag\bin\Debug\net9.0-windows10.0.19041.0\` for a dev build):

```powershell
.\lam-diag.exe session      # what the client is storing right now
.\lam-diag.exe watch        # watch the file while you sign in with "Stay signed in" ticked
.\lam-diag.exe paths        # what was detected, whether a game is running, elevation state
.\lam-diag.exe roundtrip    # prove RiotClientSettings.yaml survives being rewritten
.\lam-diag.exe collection   # read rank, collection, loot and the recovery dossier
```

All of them are read-only. `collection` exists because the two worst bugs this project has had both
rendered perfectly healthy-looking screens — printing the real figures beside the client's own is the
only check that would have caught either.

`lam-diag watch` tells you plainly whether a resumable session appeared. If it never does, this client
does not persist sessions, the app types every time, and **nothing else changes** — it is a
performance and safety optimisation, not a requirement.

---

## An optional API key

**Signing in works without one, and so does everything in the table above.** A key is useful for
exactly one thing: refreshing an account you are *not* currently signed into.

Get one at [developer.riotgames.com](https://developer.riotgames.com) and paste it into
**Settings → Riot API**. Register a **Personal** key — Development keys expire every 24 hours.

**Estimate account age** (card `⋯` menu) binary-searches match history for the oldest game. Note the
honest caveat: match-v5 only reaches back to roughly mid-2021, so for an older account the answer is
*"at least this old"* and the app says exactly that rather than presenting a floor as a birthday. The
client's real creation date, when available, is better than this in every way.

---

## Build

```powershell
dotnet build
dotnet test                                  # 313 tests

# Launch the built .exe directly. `dotnet run` cannot start it: the embedded
# requireAdministrator manifest means the dev host has to go through UAC.
.\src\LAM.App\bin\Debug\net9.0-windows10.0.19041.0\LeagueAccountManager.exe

# single self-contained .exe
dotnet publish src/LAM.App -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

```
src/LAM.Core     vault, crypto, Riot integration, login strategies, fleet analysis  ← unit-tested
src/LAM.App      WPF UI
tools/LAM.Diag   lam-diag pre-flight checks
tests/LAM.Tests  313 tests
```

If a Riot update ever breaks the field detection, the selectors and status phrases live in one file —
`login-ui.json` beside the vault (`%APPDATA%\LeagueAccountManager`). It is a data fix, not a rebuild.

---

## Three things the client data gets wrong

Recorded because each one silently produced a plausible, wrong number, and each is now commented and
locked by a test.

- **Counts are duplicated.** The game data ships `Jade_*` twins offset by 60,000 — champion `60001` is
  a copy of `1`, skin `60001008` belongs to it. Counting them turns a 173-champion roster into 236 and
  a 1,277-skin collection into 1,582. Most of the duplicate skins have no name at all, so they render
  as bare id numbers.
- **Epoch units differ per endpoint.** The catalogue reports acquisition dates in **seconds**; the
  champions and alias endpoints report theirs in **milliseconds**. Reading one as the other dates an
  entire collection to January 1970 — absurd in one direction, silent in the other.
- **`freeToPlay` does not mean "not yours".** On the champions endpoint it marks the *current
  rotation*, and a champion bought a decade ago is flagged in the week it is free. Filtering on it
  deletes champions the account owns. The identically-shaped `f2p` on the *skin inventory* means the
  opposite — a temporary grant — and filtering there is correct.

There is also a readiness race worth knowing about: the lockfile appears well before the client has
loaded its inventory, and an early read returns just the twenty champions of the free rotation. The
client says when it is ready (`/lol-inventory/v1/initial-configuration-complete`), and a capture taken
before then is refused rather than allowed to overwrite a good one.

---

## Worth knowing

- **Riot's ToS prohibits unauthorised third-party programs that interact with their client.** Account
  switchers are widespread and Riot has not historically banned for them, but the risk is not zero.
  The design keeps keystroke injection to once per account for exactly this reason. Your call.
- **An account with 2FA can never be fully hands-free.** The app pauses and asks for the code rather
  than pretending otherwise.
- **The theme is fully templated on purpose.** WPF's default control templates draw light chrome and
  inherit whatever text colour you give them, so a dark style that only sets colours yields unreadable
  text on a white popup. `ThemeTests` fails the build if a control style sets colours without
  replacing its template.
- **The master password cannot be recovered.** Take an encrypted export (**Settings → Backup**) and
  keep it somewhere other than this PC. Exports are deliberately *not* machine-bound so they open
  elsewhere — which makes the export passphrase the only thing protecting them. Use a strong one.
