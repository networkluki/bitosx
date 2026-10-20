# Account sign-in (0.8.0)

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
that means, and what it does not, is in [SYNC.md](SYNC.md).

## What it is not

The account password is **not the master password**, and must not be the same password:

| | Master password | Account password |
|---|---|---|
| Protects | the vault (derives the key that wraps the vault key) | the account on the server |
| Known to | you, and the application while it derives the key | you, and the server as an Argon2id hash |
| Leaves the device | **never** | yes, over TLS, when you sign in |
| Forgotten | the vault cannot be recovered | reset on the web, in a later milestone |

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

Never: the master password, the vault key, any item, any note. `IBitosXApiClient` has no method
that could carry them, and `AccountSession` does not know the vault beyond its lock state; the
sync passes the encrypted envelope through as the bytes it is given.

## Where the tokens are

In memory, in `AccountSession`, and nowhere else. They are dropped:

- when you sign out (the server is told; the tokens are dropped even if it cannot be reached);
- when you sign out everywhere (every session of the account ends on the server);
- **when the vault locks**, by hand, by inactivity, when the workstation locks or the system
  sleeps, or because a fault locked it — the server is told, best effort, after the tokens are
  already gone; an unattended machine then holds neither the vault nor the account;
- when the server answers 401 to a call: the server never says why, and the application does not
  guess;
- when BitosX closes.

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
  redirects followed; no cookies; a 15-second limit per call; at most 64 KiB read back — except an
  envelope download, which may be the 16 MiB a vault file may be.
- **Responses are untrusted input:** the status decides the outcome; a body is accepted only when
  it is `application/json` of the expected shape; the server's own text is never shown or logged.
  Every failure is one of a fixed set (`ApiFailure`) with a fixed message.
- **Refresh:** an access token with less than 30 seconds left is refreshed before use. The server
  ends a session whose refresh token is presented twice, so at most one refresh is in flight at a
  time; other callers wait for it.
- **Rate limits** (429) are shown with the wait the server asked for, when it said.
- **Logging:** which endpoint failed and how (`ApiLog`); never an email, a password, a token, a
  session id, or the server's text.

## Verification

Automated (`tests/BitosX.ApiClient.Tests`, `tests/BitosX.App.Tests`), for every change:

- what each call sends: method, path, JSON body, bearer header, `User-Agent`;
- the mapping of every status to a failure, with the server's text proven absent from the message;
- malformed, oversized and non-JSON answers refused as `Unexpected`;
- exactly one refresh under concurrent callers; a refused refresh and a 401 end the session;
- sign-out forgets the tokens whether or not the server can be told; the vault locking signs out;
- the Account page: validation before a round trip, the password cleared after every attempt,
  what each failure says, the signed-in state, the sessions list, and both sign-outs;
- every input on the page has a name for a screen reader; the page is drawn at both window
  sizes, signed out and signed in.

**NOT VERIFIED:** against the real server. `api.bitosx.se` is not publicly reachable yet
(`bitosx-api/docs/DEPLOYMENT.md`), so the application has only been run against the scripted
server of the tests. The contract it implements is `bitosx-api/docs/API.md`.
