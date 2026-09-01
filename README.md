# League Account Manager

A protected vault for your Riot accounts. Click a card, and it signs in.

Built for Windows, C# / WPF on .NET 9. Everything stays on your machine — there is no server, no
sync, and no telemetry.

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
  returns `***`, and a redacting serialiser masks every one of them at once. A test asserts it.
- Every save is atomic and takes a timestamped backup first, so a crash mid-write costs you the last
  save, never the vault.

---

## It asks for administrator, and why

The app requests elevation on launch. That is not enthusiasm for privilege — it is forced.

`RiotClientServices.exe` ships an `asInvoker` manifest, so in principle nothing here needs admin.
But this machine carries a Windows *"Run as administrator"* compatibility flag on it:

```
HKCU\...\AppCompatFlags\Layers
  RiotClientServices.exe       => ~ RUNASADMIN
  LeagueClient.exe             => ~ RUNASADMIN
  League of Legends.exe        => ~ RUNASADMIN
```

That makes the Riot Client run at high integrity, and Windows UIPI then forbids a normal-integrity
process from doing all three things this app needs: **typing into it**, **reading its window**, and
**closing it** — each failing with its own unrelated-looking error. Matching its level is the only
way to keep those working without altering your Riot setup.

If you ever clear those tick boxes (Properties → Compatibility on each executable), nothing needs
elevation any more and `requireAdministrator` can come back out of `src/LAM.App/app.manifest`.

**Settings → Diagnostics** reports your current integrity level and any Riot compatibility flags, so
a mismatch reads as a stated fact rather than a mystery. `lam-diag paths` prints the same thing.

---

## First run

1. Build it (see below), or run `LeagueAccountManager.exe`.
2. Choose a master password. **There is no reset.** Write it down somewhere physical.
3. Say yes to Windows Hello when offered — it becomes your everyday unlock.
4. **Add account** → name, Riot login username, password, region.
5. Click the card. The first time it types; watch it, then leave it alone.
6. After that first sign-in the app fills in the Riot ID, PUUID and level by itself, and the card
   changes from *"Types password"* to *"Instant — saved session"*.

---

## Will session-swapping work on your client?

Probably, but it was not provable from the files alone — on the machine this was built on
`riot-login: persist` was `null` and the cookie jar held only a device cookie (`tdid`), which is what
a signed-out client looks like. So there is a tool to check:

It is a command-line tool, so double-clicking it does nothing useful. Open a terminal in
`publish\diag\` (or `tools\LAM.Diag\bin\Debug\net9.0-windows10.0.19041.0\` for a dev build):

```powershell
cd "C:\Users\marcr\Desktop\league-account-manager\publish\diag"

.\lam-diag.exe session     # what the client is storing right now
.\lam-diag.exe watch       # watch the file while you sign in with "Stay signed in" ticked
.\lam-diag.exe paths       # what was detected, whether a game is running, elevation state
.\lam-diag.exe roundtrip   # prove RiotClientSettings.yaml survives being rewritten
```

All four are read-only; none of them change anything.

`lam-diag watch` tells you plainly whether a resumable session appeared. If it never does, this
client does not persist sessions, the app types every time, and **nothing else changes** — it is a
performance and safety optimisation, not a requirement. The same information is in
**Settings → Diagnostics**.

---

## Rank, level, and account age

Optional, and only for display. **Signing in works without an API key.**

Get a key at [developer.riotgames.com](https://developer.riotgames.com) and paste it into
**Settings → Riot API**. Register a **Personal** key — Development keys expire every 24 hours.

**Estimate account age** (card `⋯` menu) binary-searches match history for the oldest game. Note the
honest caveat: match-v5 only reaches back to roughly mid-2021, so for an older account the answer is
*"at least this old"* and the app says exactly that rather than presenting a floor as a birthday.

---

## The recovery dossier

You asked for anything that helps get an account back. Being precise about what is possible:

| | Where it comes from |
|---|---|
| PUUID, Riot ID, summoner/account id, level, icon | **Automatic** — the League client, right after sign-in |
| Rank (solo + flex), last activity | **Automatic** — Riot API |
| Every Riot ID the account has used | **Automatic** — PUUID is stable, so renames are detected |
| Account age (a lower bound) | **Automatic** — oldest match on record |
| Email, phone, real creation date, how you got it, first champion bought, purchase receipts, 2FA backup codes, security answers | **Typed once, by you** — no Riot API exposes any of it |

The **PUUID is the single most valuable field**, because it survives every rename. It is captured for
free on the first sign-in.

Each card shows a warning when the dossier has a hole in it — no recovery email, an email you have
said you no longer control, or 2FA enabled with no backup codes stored. **Recovery sheet** (card `⋯`
menu) renders the lot as one page ordered the way a support ticket wants it, and **omits secrets by
default** so it is safe to paste.

---

## Build

```powershell
cd "C:\Users\marcr\Desktop\league-account-manager"
dotnet build
dotnet test                                             # 74 tests

# Launch the built .exe directly. `dotnet run` cannot start it: the embedded
# requireAdministrator manifest means the dev host has to go through UAC.
.\src\LAM.Appin\Debug
et9.0-windows10.0.19041.0\LeagueAccountManager.exe

# single self-contained .exe
dotnet publish src/LAM.App -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

```
src/LAM.Core     vault, crypto, Riot integration, login strategies   ← all the logic, unit-tested
src/LAM.App      WPF UI
tools/LAM.Diag   lam-diag pre-flight checks
tests/LAM.Tests  74 tests
```

If a Riot update ever breaks the field detection, the selectors and status phrases live in one file —
`login-ui.json` beside the vault (`%APPDATA%\LeagueAccountManager`). It is a data fix, not a rebuild.

---

## Worth knowing

- **Riot's ToS prohibits unauthorised third-party programs that interact with their client.** Account
  switchers are widespread and Riot has not historically banned for them, but the risk is not zero.
  The design keeps keystroke injection to once per account for exactly this reason. Your call.
- **An account with 2FA can never be fully hands-free.** The app pauses and asks for the code rather
  than pretending otherwise.
- **The theme is fully templated on purpose.** WPF's default control templates draw light chrome
  and inherit whatever text colour you give them, so a dark style that only sets colours yields
  unreadable text on a white popup. `ThemeTests` fails the build if a control style sets colours
  without replacing its template.
- **The master password cannot be recovered.** Take an encrypted export (**Settings → Backup**) and
  keep it somewhere other than this PC. Exports are deliberately *not* machine-bound so they open
  elsewhere — which makes the export passphrase the only thing protecting them. Use a strong one.
