# Hextech Manager

A protected vault for your Riot accounts. Click a card, and it signs in.

Windows, C# / WPF on .NET 9. No server, no sync, no telemetry, no account, and no API key required
for anything that matters. Your vault never leaves the machine.

*(The repository is still `league-account-manager`, and the assembly is still
`LeagueAccountManager.exe`. The product name changed; the identifiers did not.)*

**Download:** [latest release](https://github.com/MarceloVarela/league-account-manager/releases/latest).
`HextechManager.exe` is one self-contained file. Windows will warn that it is unsigned — *More info* →
*Run anyway*.

## Requirements

- Windows 10 1809 or later, 64-bit
- The Riot Client, installed and run at least once
- **Administrator.** Not optional, and [there is a reason](#it-asks-for-administrator-and-why)
- Nothing else. The download bundles its own .NET

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
| **The last ten ranked games**, drawn as the form chart on the card | ↑, when the client serves it in time |
| **Account created**, last password change, legacy login username, original region | ↑ |
| **Every Riot ID the account has used**, with the date each was taken — Riot's own record | ↑ |
| Registered email (masked by Riot), phone (country code + last 4), 2FA, region history | ↑ |
| First champion and first skin ever bought, with dates | derived from purchase dates |
| Recovery email in full, purchase receipts, 2FA backup codes, security answers | **typed by you** — nothing local exposes them |

The last row is short on purpose. The League client's entire RPC surface — all 1,465 functions — was
enumerated looking for purchase or receipt history. **There is none.** `/lol-inventory/v1/wallet/transactions`
looks like it would be and is a false positive. So the earliest-purchase reference stays a field you
fill in, and the app does not pretend otherwise.

**The form chart can be missing, and that is not a fault.** Match history is read last in the
sign-in pass, deliberately: the client serves it late, and it is the one thing here worth abandoning
rather than delaying the rest. So it is absent on an account captured before this existed, and it can
simply not arrive in time — signing in again fills it. When it is absent the card shows the **season**
totals with `SEASON` beside them, and draws no bars, because a ten-bar chart built from a season total
would be a picture of data that does not exist.

**Recovery sheet** (card `⋯` menu) renders the lot as one page ordered the way a support ticket wants
it, and **omits secrets by default** so it is safe to paste.

---

## Getting around

**The grid** is one card per account: rank in its own tier colour, LP, level, champions and skins
owned, essence, honour, and when the client data was last read. Search matches name, tagline, region,
tags and rank. Sort cycles last used → rank → name.

**Cards warn about themselves.** An account with no recovery email, with two-factor but no backup
codes, or that Riot reports as disabled says so on the card. Warnings can be dismissed per account —
the `✕` on the plate — and *Show hidden warnings* on the card's `⋯` menu brings them back. Dismissing
hides the badge, not the problem: it still appears in the account's details.

**Quick swap** lives on the tray icon and on `alt + \`, from anywhere including inside a game. One row
per account, rank in its tier colour, click to sign in.

**Minimise to tray** is a setting, off by default, so minimising keeps the app in the taskbar until you
say otherwise.

**Dark and light** both ship; the theme switch is in Settings and applies to the lock screen too.

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

## Carrying your keybinds between accounts

League stores hotkeys, HUD layout and video options **per account** and resets them on every switch —
enough of a nuisance that tools exist which do nothing else. **Settings → League settings** turns it
on: save your setup once, and it is restored after each sign-in.

This is the only feature that writes into the game's own configuration, so it is the most destructive
thing in the app and is built accordingly:

- **Off unless you turn it on**, and it does nothing at all until you press *Save current settings*.
- **A pristine copy** of `PersistedSettings.json`, `game.cfg` and `input.ini` is taken before the first
  ever write, and *Revert to originals* puts them back.
- **Close League first.** It holds those files open and rewrites them on exit, so a restore performed
  while it is running is simply overwritten. Restoring refuses to run in that state rather than
  pretending to have worked.

---

## Vault safety

- **Deleting is reversible.** *Move to trash* hides an account but keeps its password, session and
  recovery dossier. The dossier is the one thing here that signing in again cannot rebuild.
  ⚠ There is currently no button to restore or to purge a trashed account — the data is kept and
  nothing is lost, but getting it back means editing the vault. Both are on the list.
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

## Stealth login — appearing offline

Optional, **off by default**. Makes an account appear **offline**, **on mobile**, or **online** to its
friends list, while the session stays completely normal: you are signed in, you can play, and your own
chat works.

League has no "appear offline". The way every tool in this space provides one is the same: stand
between the League client and Riot's chat server on your own machine, and edit the presence the client
announces. This app does that, and nothing else — it reads no game memory, modifies no game files, and
never touches Vanguard or any anti-cheat.

```
                  ┌── config: "chat lives at localhost.marcrake.lol" ──┐
League client ────┤                                                    ├──► Riot
                  └── chat: <show>chat</show> ──► <show>offline</show> ─┘
```

The client accepts the local connection because the certificate is genuine, not because anything is
bypassed: `localhost.marcrake.lol` is a real public hostname whose address record is `127.0.0.1`, with
a real Let's Encrypt certificate for it. Riot stopped honouring the client's own
`chat.allow_bad_cert.enabled` flag in April 2026, so this is now the only route that works at all.

**A friend called *Hextech Manager*** appears at the top of your friends list once it is running.
Message it `offline`, `mobile` or `online` to change how you appear without leaving the game; `status`
and `help` also work. Nothing you send it ever reaches Riot — there is no such account. The same
options are on the tray icon.

### What it will not do

**It fails open, always.** If the hostname does not resolve, or the certificate is unusable, or a port
will not bind, the client launches **untouched** and signs in normally, and the status bar says so.
Once the client has been handed a rewritten configuration it no longer knows where the real chat server
is, so "half on" is not a state that can exist — it is fully working before the client starts, or it
does not happen.

**It never logs a stanza.** Your account's authentication token crosses the proxy in plain text. A test
reads the source and fails the build if any trace call could carry a stanza, a body or an authorization
header into `login.log`.

### Two things to know before you switch it on

**Lobby chat is a trade.** The presence that makes lobby and champion-select chat work is addressed at
the room, not at your friends list, so it is passed through untouched — which means a lobby can see a
status your friends list is not being told. **Settings → lobby chat while hidden** turns that off too,
and lobby and champion-select chat then stop working entirely. On by default.

**Riot's position is unknown.** Riot has never published one on this technique, and there is no
documented case of it being actioned — after years of a very widely used tool doing the same thing.
That is not the same as approval. It is off by default, and the choice is yours to make deliberately.

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
  works. Hello enrolment signs a challenge twice and refuses if the signature is not reproducible,
  because a scheme that enrolled cleanly and then failed to unlock would fail at the worst possible
  moment.
- ⚠ **Machine binding is the exception to that.** With *bind this vault to this Windows profile* on
  (**Settings → advanced**), the encrypted body is additionally wrapped with DPAPI for your Windows
  user — and then a wiped profile, a reinstall or a new machine means the vault cannot be opened
  **even with the correct master password**. That is the point of the setting: a copied `vault.dat` is
  useless elsewhere. It is also the one way to lose everything while doing nothing wrong, so take an
  export before you reinstall Windows.
- **Tamper-evident.** The header is fed to AES-GCM as associated data, so flipping one byte anywhere —
  including downgrading the recorded Argon2 cost — fails authentication instead of silently weakening
  the vault.
- **Auto-locks** on idle, on Windows locking, and optionally on minimise. The key is zeroed on lock.
- **Secrets never reach a log.** Passwords and backup codes are typed `SecretText`, whose `ToString()`
  returns `***`, and a redacting serialiser masks every one of them at once. Tests assert it, and that
  the CSV export contains no secret of any kind.
- Every save is atomic and takes a timestamped backup first, so a crash mid-write costs you the last
  save, never the vault.

### Everything that leaves the machine

There is no server, no account and no telemetry, but "no network" would be a lie. The complete list:

| Where | When | What is sent |
|---|---|---|
| `ddragon.leagueoflegends.com` | an account's profile icon is not cached yet | the icon id. Riot's own public CDN, no key |
| `raw.communitydragon.org` | a skin tile is not cached, and League is closed | the asset path. Falls back from the running client, which is preferred |
| `github.com/.../releases/latest` | **stealth only**, and only when the certificate it shipped with is near expiry | nothing but the request |
| `clientconfig.rpg.riotgames.com`, `riot-geo.pas.si.riotgames.com` | **stealth only** | the client's *own* request, forwarded with its own headers — the same call it would make directly |
| `*.api.riotgames.com` | **only if you enter an API key**, which nothing requires | your key and a Riot ID |

Both caches are once-per-asset and go quiet afterwards; the app works offline once warmed. Nothing in
that table carries a vault password, a session, a recovery answer or an identifier for you.

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

**Settings → advanced** reports your current integrity level and any Riot compatibility flags, so a
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
League, so it should never happen because a window was opened. **LOG IN** on the card footer is the
button that signs in.

Two things worth finding early: the tray icon carries **quick swap**, and `alt + \` opens the same
list from anywhere, including from inside a game. Neither needs the window.

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
**Settings → riot api key**. Register a **Personal** key — Development keys expire every 24 hours.

**Estimate account age** (card `⋯` menu) binary-searches match history for the oldest game. Note the
honest caveat: match-v5 only reaches back to roughly mid-2021, so for an older account the answer is
*"at least this old"* and the app says exactly that rather than presenting a floor as a birthday. The
client's real creation date, when available, is better than this in every way.

---

## Build

```powershell
dotnet build
dotnet test                                  # 386 tests

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
tests/LAM.Tests  386 tests
```

**The stealth certificate is not in the repo.** `src/LAM.App/Assets/stealth.pfx` is git-ignored: it is
a real certificate for a hostname only this project controls, and it expires every ninety days. The
build and the tests are both fine without it — stealth simply reports itself unavailable and sign-in
proceeds exactly as normal.

If a Riot update ever breaks the field detection, the selectors and status phrases live in one file —
`login-ui.json` beside the vault (`%APPDATA%\LeagueAccountManager`). It is a data fix, not a rebuild.

---

## What lands on disk

Everything lives in `%APPDATA%\LeagueAccountManager`:

```
vault.dat          your accounts, AES-GCM encrypted. The only file that holds secrets
backups/           rolling copies of the above, taken before every save
logs/login.log     a sign-in trace: stage names, window handles, process ids, outcomes
theme              which palette to draw the lock screen in, before anything is decrypted
lockfacts          account count and idle-lock minutes, for the same reason
stealth.pfx        a cached copy of the stealth certificate, if a fresher one was fetched
stealth-greeted    an empty file. Delete it and the in-game friend introduces itself once more
login-ui.json      the login-form selectors, if you ever need to fix them without a rebuild
```

`login.log` is **not encrypted** and is meant to be readable — it exists so a failed sign-in can be
diagnosed. It carries no password, no username, no session and no token; the discipline is enforced by
tests, not by hope. Everything worth stealing is in `vault.dat`.

---

## Maintaining it

**The stealth certificate expires every ~90 days.** When it does, stealth stops working for everyone
and nothing says why — the friends list simply shows people online. The current one expires
**5 December 2026**; Let's Encrypt suggests renewing from **5 November**.

Renewing it:

```bash
# 1. acme.sh prints a TXT record; add it at the DNS host for marcrake.lol
~/.acme.sh/acme.sh --issue --dns -d localhost.marcrake.lol --keylength ec-256     --server letsencrypt --yes-I-know-dns-manual-mode-enough-go-ahead-please

# 2. once the record resolves
~/.acme.sh/acme.sh --renew -d localhost.marcrake.lol --ecc     --yes-I-know-dns-manual-mode-enough-go-ahead-please

# 3. bundle it WITH the chain and no password
D=localhost.marcrake.lol
openssl pkcs12 -export -out stealth.pfx -inkey ~/.acme.sh/${D}_ecc/$D.key     -in ~/.acme.sh/${D}_ecc/$D.cer -certfile ~/.acme.sh/${D}_ecc/ca.cer -passout pass:
```

Then put it in `src/LAM.App/Assets/stealth.pfx` **and** attach it to a new GitHub release as
`stealth.pfx`. The app fetches `releases/latest/download/stealth.pfx` when the copy it shipped with is
within 20 days of expiry, so existing installs pick up the new one without anyone reinstalling — which
is the entire reason it is published rather than only embedded.

Three things that will bite:

- It must be **DNS-01**. The hostname resolves to `127.0.0.1`, so nothing can reach it over HTTP to
  verify — HTTP-01 cannot work, ever.
- **Never a wildcard.** The private key ships inside the app and is public by construction. It is
  harmless only because the one name it vouches for can never resolve anywhere but the user's own
  machine.
- The `-certfile` matters. Without the chain the client has to build the path itself, which is slower
  and fails outright on a machine that cannot.

The test suite fails once the shipped certificate is inside that 20-day window, so this cannot slip
past quietly — but only if someone runs it.

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
- **Importing merges, and it merges by replacing.** An account already in the vault with the same id is
  **replaced whole** by the one in the file rather than having its fields merged, so importing an old
  export rolls those accounts back to what they were when it was taken. Re-importing your own current
  export is therefore harmless, but an old one is not. The confirmation says how many would be added
  and how many replaced before anything is written — read that number.
