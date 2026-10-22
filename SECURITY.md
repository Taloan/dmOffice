# Security Policy

## Reporting a vulnerability

Please report security issues privately, not in a public issue:

- Use GitHub's **private vulnerability reporting** (the "Report a vulnerability" button under the **Security** tab), or
- email the address in the maintainer's GitHub profile.

Include what you found, how to reproduce it, and the impact. We will acknowledge receipt and keep you posted on the fix.

## Running dmOffice safely

- **Always put dmOffice behind a TLS reverse proxy.** The container speaks plain HTTP on port 8080; never expose that port directly on the open internet. The example `compose.yaml` binds it to `127.0.0.1`.
- **Set `DMOFFICE_MASTER_KEY`** (e.g. `openssl rand -hex 32`) and keep it outside the data volume. Without it, dmOffice writes the key as a file next to the data, and anyone with a copy of the volume or a backup can decrypt everything.
- Behind a proxy, set **`DMOFFICE_TRUSTED_PROXIES`** to the proxy's address, so only it may send `X-Forwarded-*` headers.
- Requests **without** login are rate-limited per address with a rising block (30 s, doubling up to 1 hour; see `DMOFFICE_ANON_LIMIT`). This only works if dmOffice sees the real client address, so `DMOFFICE_TRUSTED_PROXIES` matters.
- The **first account** created on a fresh install is the admin. dmOffice prints a one-time setup code to the log (`docker logs`); enter it to create that account.
- Keep the image up to date so base-image and OCR-tool security updates arrive.

## Checking dependencies

```bash
dotnet list DmOffice.Server/DmOffice.Server.csproj package --vulnerable --include-transitive
npm --prefix web audit
```
