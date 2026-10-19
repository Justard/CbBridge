# Licensing

CB Test Bridge is **proprietary**: all rights belong to the copyright holder (see [LICENSE](../LICENSE)). Using it
needs a **licence key** issued by the copyright holder. This page is for the copyright holder (making keys) and for
administrators (installing them).

- [How the lock works](#how-the-lock-works)
- [One-time setup (copyright holder)](#one-time-setup-copyright-holder)
- [Issuing a licence key](#issuing-a-licence-key)
- [Installing a licence key (administrator)](#installing-a-licence-key-administrator)
- [What the lock does and does not protect](#what-the-lock-does-and-does-not-protect)

## How the lock works

| | |
|---|---|
| Key pair | ECDSA P-256. The **private key** stays with the copyright holder, encrypted with a passphrase, never in the repository. The **public key** is built into the program. |
| Licence key | `CBB1.<content>.<signature>` — licensee, ID, issue date, optional expiry, optional machine names, signed with the private key. Changing one character makes it invalid. |
| Without a valid key | The bridge shows only the licence page. Console, history, CI API, `/api/v1`, `/mcp` and `/metrics` are refused (HTTP 402); test runs never start. `/health` keeps answering. |
| Expiry | 30 days before, a yellow notice appears on every page. On the day, the bridge locks — without a restart. |
| Machine binding | Optional: a key can name the machines (`Environment.MachineName`) it may run on. In Docker set a fixed `--hostname`. |
| Development | When `ASPNETCORE_ENVIRONMENT=Development` the licence is shown but not enforced, so you can develop and test. `Licensing:EnforceInDevelopment=true` turns it on there too. |

## One-time setup (copyright holder)

On **your own PC**, inside the repository:

```bash
dotnet run --project tools/LicenceTool -- keygen
```

- Asks for a passphrase (at least 12 characters).
- Writes the **private key** to `~/.cbbridge/licence-private.pem` (Windows: `C:\Users\<you>\.cbbridge\`), encrypted.
- Writes the **public key** to `src/CbBridge.Core/Licensing/licence-public-key.pem`.

Then:

1. **Back up the private key and the passphrase** offline (e.g. a USB stick in a drawer, plus a password manager).
   Lost = no new keys for builds that contain this public key. Leaked = anyone can make keys; make a new pair and
   release a new build.
2. Commit the **public** key (on a `chore/` or `feature/` branch, as usual). From now on every build only accepts
   keys signed by your private key.

`*private*.pem`, `*.lic` and `licence.key` are in `.gitignore`, so they are not committed by accident.

## Issuing a licence key

```bash
dotnet run --project tools/LicenceTool -- issue --licensee "Example GmbH" --email it@example.com --days 365 --out example.lic
dotnet run --project tools/LicenceTool -- issue --licensee "Lab bench server" --host BENCH-SRV-01 --expires 2027-12-31
dotnet run --project tools/LicenceTool -- verify example.lic
```

| Option | Meaning |
|---|---|
| `--licensee` | Who it is for (shown in the app) |
| `--days N` / `--expires YYYY-MM-DD` | Validity; neither = no expiry |
| `--host NAME` | Only on this machine (repeat for several); none = any machine |
| `--email`, `--notes` | For your records; shown on the licence page |

Keep a list of what you issued (ID, licensee, dates). Keys can't be revoked one by one — give short validity
(e.g. one year) and issue new keys on renewal.

## Installing a licence key (administrator)

Any one of:

- **Licence page:** open the bridge, paste the key, *Check and install*. It is checked first and saved as
  `licence.key` next to the app.
- **File:** put the key into `licence.key` in the app folder (or set `Licensing:File`).
- **Environment variable:** `Licensing__Key=CBB1.…` (containers, systemd).

The licence page (`/licence`) and the footer show who it is licensed to and until when; `GET /api/v1/status`
includes the licence state.

| Setting | Default | Meaning |
|---|---|---|
| `Licensing:Key` | *(empty)* | The key itself |
| `Licensing:File` | `licence.key` | File holding the key, relative to the app folder |
| `Licensing:EnforceInDevelopment` | `false` | Enforce also in Development mode |

## What the lock does and does not protect

- **It protects the programs you hand out.** Someone running a build without your key cannot use it, and cannot make
  a key — the private key is not in the program.
- **It does not protect the source code.** Anyone who has the source can remove the check and build their own copy.
  The source is protected by the **licence terms** (copyright) and by **keeping the repository private**: on a
  public GitHub repository, GitHub's terms let every user view and fork it, whatever the licence says.
- No licence check survives a determined attacker with the binaries either; the signature makes forging keys
  impractical, but code can be patched. Combine it with a written agreement for every licensee.
- Who owns the software in the first place depends on how and where it was made. If it relates to your job, have
  your employment contract checked — a licence file can't change that.
