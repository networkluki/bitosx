# Account sign-in (0.8.0), the account itself (0.11.0), and passkeys on the web (0.13.0)

Since 0.8.0 the desktop application can sign in to a **BitosX account** on the BitosX server
(`api.bitosx.se`). This document says what that is, what it is not, what leaves the device, and
how it fails.

## What it is

An account is the thing the server knows you by: an email address and an **account password**,
created on the web at `vault.bitosx.se` (the address is confirmed by email before a password can
be chosen). The Account page of the application signs in with them, shows the account and its
sessions — every device and browser that is signed in — and can end any of them, this one
included.

That was all it did in 0.8.0. Since 0.9.0 the account also carries the **encrypted sync**: the
vault file, as encrypted on the device, goes to the server and comes back as opaque bytes. What
that means, and what it does not, is in [SYNC.md](SYNC.md). Since 0.11.0 the page can also change
the account password, delete the account, show whether two-factor authentication is on, and open
the page that resets a forgotten account password. Since 0.13.0 it also shows how many passkeys
the account has; passkeys themselves are used on the web vault, not here.

## What it is not

The account password is **not the master password**, and must not be the same password:

| | Master password | Account password |
|---|---|---|
| Protects | the vault (derives the key that wraps the vault key) | the account on the server |
| Known to | you, and the application while it derives the key | you, and the server as an Argon2id hash |
| Leaves the device | **never** | yes, over TLS, when you sign in |
| Forgotten | the vault cannot be recovered | reset at vault.bitosx.se ("Forgot your password?" on the Account page) |
| Changed | Settings → Change master password, on this device | Account → Change account password |

If the two were the same, the server — or anyone who compromised it — would hold the key to the
vault. They are different systems on purpose, in the application as on the server (see
`docs/SECURITY_MODEL.md` of `bitosx-api`).

## What leaves the device

On sign-in: the account email and the account password, in the body of one HTTPS request to
`POST /v1/auth/login`. After that, only the tokens the server issued: a short-lived access token
in the `Authorization` header of each call, and the refresh token in the body of a refresh. The
`User-Agent` header carries the product name and version and the operating system
(`BitosX/0.8.0 (Windows)`), which the server records as the session's client description so
that a session can be told apart in the list.

Since 0.9.0, when you press *Sync now*: the vault file as it is on disk — ciphertext and public
parameters, Base64 in the body of one request — and the revision it is based on ([SYNC.md](SYNC.md)).

Since 0.11.0, when you change the account password: the current and the new account password
(and, with two-factor on, a code) in the body of `POST /v1/account/password`. When you delete the
account: the account password (and a code) in the body of `POST /v1/account/delete`.

Never: the master password, the vault key, any item, any note. `IBitosXApiClient` has no method
that could carry them, and `AccountSession` does not know the vault beyond its lock state; the
sync passes the encrypted envelope through as the bytes it is given.

## Second factor (0.10.0)

An account can have two-factor authentication (TOTP, set up and managed on vault.bitosx.se). The
application then signs in in two steps: `POST /v1/auth/login` with email and password answers
**202** with a short-lived challenge token instead of a session, the page asks for the code from
the authenticator app — 6 to 8 digits, as many as the server uses — or one of the recovery codes
(ten characters, a hyphen or space after the fifth optional, any case), and `POST /v1/auth/login/totp`
with that token and the code returns the session's tokens. Between the two steps there is no
session: nothing authenticated can be called, only the challenge token is held (memory, at most
five minutes), and locking the vault, cancelling, or signing in again drops it. A refused code
can be retried (the server allows a few attempts, then the password must be entered again); the
page never says why a code was refused beyond "not accepted". What leaves the device in step two
is the challenge token and the code, nothing else. The second factor protects the account sign-in
only; it has nothing to do with the vault master password.

Since 0.11.0 the Account page shows the state read-only (`GET /v1/account/totp`: on since when, how
many recovery codes are left); a server that does not offer two-factor (503 or 404) is shown as
"not available". Setting it up and turning it off stays on vault.bitosx.se. A sign-in step whose
challenge has expired says so and returns to the password, instead of asking for "the next code".

## Passkeys (0.13.0)

An account that has two-factor authentication can also have **passkeys** (WebAuthn), added on
vault.bitosx.se. They come on top of the authenticator code and never replace it: there, a
passkey can be the second step after the password, or sign in without email and password at all.
**This application does not use them.** It has no WebAuthn, never asks the operating system for
a passkey, and keeps signing in with the account password and the code from the authenticator
app or a recovery code. A passkey authenticates the account only; the vault master password has
nothing to do with it.

What the application does with passkeys:

- **The second step says so.** Since 0.13.0 the 202 of `POST /v1/auth/login` carries `methods`, the
  ways the step can be completed (`"totp"`, `"recovery"`, and `"passkey"` when the account has
  passkeys). It is read tolerantly: no `methods` (a server before passkeys) means the code step, as
  before; names this client does not know, and empty entries, are ignored; a list of more than 32
  entries, a `methods` that is not a list of names, or one that offers neither `"totp"` nor
  `"recovery"` — a step this application could not complete — is refused as an unexpected answer.
  When `"passkey"` is listed, the page adds: *"This account also has passkeys. They are used on
  vault.bitosx.se; here, enter the code from your authenticator app or a recovery code."* (for
  another server, "the web vault of this server"). Nothing else changes: the code goes to
  `POST /v1/auth/login/totp` as before.
- **The Account page shows how many.** `GET /v1/account/passkeys` (ordinary deadline and 64 KiB
  cap; the server lists at most 10) gives the list; only its length is shown, in the two-factor
  card: *"Passkeys: 2 registered — use them on vault.bitosx.se; this app signs in with your
  authenticator code."*, or *"Passkeys: none registered. …"*. The list carries a name, when the
  passkey was added and last used, and whether it is backed up — never key material or a
  credential id — and is validated like every other answer (at most 100 entries, names of at most
  128 characters, every field present). A server without passkeys answers 404: the line is not
  shown. Any other failure does not fail the page; the line then says the count could not be read.
  Adding, renaming and removing passkeys stays on the web vault.

## Changing the account password (0.11.0)

*Account → Change account password* asks for the current account password, the new one twice
(12 to 128 characters, used exactly as typed) and — when the account has two-factor on, or when
that could not be read — a code from the authenticator app or a recovery code. The server
(`POST /v1/account/password`) replaces the password, **ends every other session of the account**
(every other device and browser has to sign in again) and keeps this one signed in; open
password-reset links stop working. A wrong current password or code is one generic refusal
(403, `ApiFailure.Forbidden`): nothing changed, and the session is still valid. The vault master
password is not touched — the server does not know it — and the page says so. When no answer
comes, or one that makes no sense, the change may or may not have gone through, and since 0.12.0
the page says exactly that. The fields are emptied after every attempt.

## Deleting the account (0.11.0)

*Account → Delete account* asks for the account password, a code when two-factor is on, and an
explicit acknowledgement. The server (`POST /v1/account/delete`) deletes the account with its
sessions and the **encrypted vault stored on the server**; the application then signs out here.
**The vault file on this device is not touched**: it stays, and it opens with the master password
as before. A wrong password or code is the same generic refusal; nothing is deleted. Without an
answer the page says that the account may or may not have been deleted (0.12.0).

## A forgotten account password

"Forgot your password?" under the sign-in form opens `https://vault.bitosx.se/forgot-password` in
the system browser. It is shown only for the production server: for a server given with
`--api-server`, no address is guessed. A reset changes the account password only; a forgotten
master password still cannot be recovered.

## Where the tokens are

In memory, in `AccountSession`, and nowhere else. They are dropped:

- when you sign out (the server is told; the tokens are dropped even if it cannot be reached, and
  the page then says that the server did not confirm it, so it may still list the session);
- when you sign out everywhere — **after** the server confirmed that every session of the account
  ended. If the server refused (rate-limited, unavailable), no session was ended, this one is kept,
  and the page says to try again (0.11.0). If no answer came, or one that makes no sense, the
  request may have gone through: since 0.12.0 the page says that the server did not confirm it,
  rather than that no session was ended;
- when you end this device's session from the list, or delete the account;
- **when the vault locks**, by hand, by inactivity, when the workstation locks or the system
  sleeps, or because a fault locked it — the tokens are gone at once, and then the server is told,
  best effort. Since 0.11.0 that also works when the access token has already expired, which an
  inactivity lock always comes after: the refresh token is spent for a new access token, and that
  one ends the session. Before, an inactivity lock left the server session (and its 30-day refresh
  token) alive;
- when the server answers 401 to a call twice: once, the session is refreshed and the call
  retried (0.11.0); if the refresh is refused or the retry is answered 401 too, the session is over.
  The server never says why, and the application does not guess;
- when the refresh token expires (30 days idle, 90 days in all) — like every other sign-out, the
  pages follow (0.11.0);
- when BitosX closes: the tokens are forgotten and the server is told, waiting at most two seconds
  for it (0.11.0; before, closing told the server nothing).

**A session that ended stays ended.** Every sign-out, lock and sign-in starts a new generation of
the session. A sign-in, second-factor step or refresh that the server answers after the vault
locked (or after a sign-out) does not bring tokens back: they are discarded, and the session they
open is ended on the server (0.11.0).

The next run signs in again. OS-protected storage for the refresh token (`ISecureStorage`:
DPAPI, Keychain, Secret Service) is a later milestone; until then a session lives as long as the
window.

Managed .NET strings cannot be wiped reliably (`docs/SECURITY_MODEL.md`). The tokens are
short-lived and dropped as early as possible; that is what can be done.

## How the client behaves

- **One server, fixed in the build:** `https://api.bitosx.se`. `--api-server https://host[:port]`
  points a run at another server (plain `http://` only to this machine, for development);
  `--api-server none` makes the run local-only, as every version before 0.8.0 was. A value that
  cannot be used ends the application with a usage error, never a quiet fallback to production.
- **Transport:** TLS as the operating system validates it, with online revocation checking; no
  redirects followed; no cookies. Every call has one deadline that covers connecting, sending, the
  headers **and the body** — 15 seconds, 120 seconds for an envelope transfer (0.11.0; before, a
  body that stopped arriving could hold the sync or the refresh for good). At most 64 KiB is read
  back — except the session list (256 KiB: the server lists at most 200 sessions) and an envelope
  download, which may be the 16 MiB a vault file may be.
- **Timestamps** are read as ISO 8601; one without an offset is taken as UTC (the server always
  sends `Z`).
- **Responses are untrusted input:** the status decides the outcome; a body is accepted only when
  it is `application/json` of the expected shape; the server's own text is never shown or logged.
  Every failure is one of a fixed set (`ApiFailure`) with a fixed message. A 403 on a
  password-confirmed call is `Forbidden` — "the password or code was not accepted", never a
  sign-out; on any other call it is "The server refused the request." (0.12.0); a 503 is "temporarily unavailable", and is blamed on two-factor only on the code step.
- **Refresh:** an access token with less than 30 seconds left is refreshed before use. The server
  ends a session whose refresh token is presented twice, so at most one refresh is in flight at a
  time; other callers wait for it.
- **Rate limits** (429) are shown with the wait the server asked for, when it said.
- **Logging:** which endpoint failed and how (`ApiLog`); never an email, a password, a token, a
  session id (the revoke call is logged as `v1/account/sessions/{id}`), or the server's text. The
  token records print `<redacted>` for their secrets.
- **Threads:** the session raises its events on whichever thread a server call completed on; the
  pages apply them on the UI thread (0.11.0).

## Verification

Automated (`tests/BitosX.ApiClient.Tests`, `tests/BitosX.App.Tests`), for every change:

- what each call sends: method, path, JSON body, bearer header, `User-Agent`;
- the mapping of every status to a failure, with the server's text proven absent from the message;
- malformed, oversized and non-JSON answers refused as `Unexpected`;
- exactly one refresh under concurrent callers; a refused refresh ends the session; a 401 is
  refreshed and retried once, and ends the session when the retry fails too; a 403 does not;
- sign-out forgets the tokens whether or not the server can be told and says which; sign out
  everywhere fails loudly and keeps the session when the server was not told; the vault locking
  and closing sign out on the server, refreshing first when the access token has expired; a
  sign-in, code step or refresh that completes after the lock brings nothing back;
- a body that stops arriving and headers that never come end at the deadline; an envelope transfer
  has the longer one; offset-less timestamps are UTC; a 200-session list is read; no session id or
  password in the log; the token records print no secret;
- changing the account password and deleting the account: what is sent, the code only when given,
  a refusal that keeps the session, the page's validation and its wording;
- passkeys (0.13.0): the challenge's `methods` with and without `"passkey"`, absent, with unknown
  names, and refused when no code step is offered or it is not a list; the passkey list read,
  empty, hidden on 404, a failure on 503, malformed, oversized and stalled answers refused; the
  page's count line, its wording for the production server and another, its absence on 404, and
  the second-step note shown only when `"passkey"` is offered, with the code still signing in;
- the Account page: validation before a round trip, the password cleared after every attempt,
  what each failure says, the signed-in state, the sessions list, and both sign-outs;
- every input on the page has a name for a screen reader; the page is drawn at both window
  sizes, signed out and signed in;
- the whole application against a server whose answers complete on other threads: signing in,
  syncing and signing out, with every change on the page made on the UI thread.

**NOT VERIFIED:** against the real server. Changing the account password, the code in account
deletion, the two-factor state and revisions that never restart need the server of phase 8
(`bitosx-api`) deployed. `api.bitosx.se` is not publicly reachable yet
(`bitosx-api/docs/DEPLOYMENT.md`), so the application has only been run against the scripted
server of the tests. The contract it implements is `bitosx-api/docs/API.md`. The passkey part
(0.13.0) has not run against the stack of bitosx-api with passkeys yet: the live test that
registers a passkey and signs in with a code (`tests/BitosX.LiveTests`) is written, and its
software authenticator was checked against the WebAuthn library the server uses, but it has not
run against the server.
