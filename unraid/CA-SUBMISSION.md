# Submitting dmOffice to Unraid Community Applications (CA)

[Deutsch](CA-SUBMISSION.de.md) · **English**

These steps need your own accounts (GitHub, Unraid forum) and therefore have to be done by hand.
Current rules: <https://ca.unraid.net/submit/help> (they take precedence over older forum posts).

## Prerequisites (done in the repository)

- Public GitHub repository `Taloan/dmOffice` with an OSI license (`LICENSE`, AGPL-3.0).
- `ca_profile.xml` in the root with a non-empty `<Profile>`.
- One template per app: `unraid/dmoffice.xml` with `<Repository>ghcr.io/taloan/dmoffice:latest</Repository>`, `<TemplateURL>` pointing to exactly this file (raw) and `<Icon>` pointing to `unraid/dmoffice.png`.
- The image is built by `.github/workflows/docker.yml`.

## 1. Check the image publicly

1. GitHub → profile → *Packages* → `dmoffice` → *Package settings* → *Change visibility* → **Public**.
2. Test without signing in (e.g. on the Unraid server):

   ```bash
   docker pull ghcr.io/taloan/dmoffice:latest
   ```

3. Install the template once yourself: copy `unraid/dmoffice.xml` to `/boot/config/plugins/dockerMan/templates-user/my-dmoffice.xml`, then *Docker* → *Add Container* → *Template* `dmOffice`, start it, open the web interface and create the admin account.

## 2. Support thread in the Unraid forum

Optional: according to <https://ca.unraid.net/submit/help> a forum thread is not mandatory. Until there is one, `<Support>` points to the GitHub issues.

Only “Community Developers” may start new topics in **Application Support → Docker Containers** (<https://forums.unraid.net/forum/47-docker-containers/>). If the *Start new topic* button is missing, the pinned rule applies (<https://forums.unraid.net/topic/40696-why-cant-i-post-new-topics-in-here/>):

1. Sign in at <https://forums.unraid.net>.
2. Send the moderators (Squid, or SpencerJ if necessary) a private message with the link to the repository `https://github.com/Taloan/dmOffice` and ask to be enabled as a Community Developer or for the thread. Alternatively create the thread in an open section and ask for it to be moved to *Docker Containers*.
3. Once enabled, create a new post in **Docker Containers**, title e.g. **“[Support] dmOffice – self-hosted mail, calendar, contacts, tasks, notes and files with CalDAV/CardDAV/WebDAV”**. Write in English, which is the audience of CA.
4. Content (suggestion):
   - Short description (as `<Overview>` in the template), one or two screenshots.
   - Links: project <https://github.com/Taloan/dmOffice>, image `ghcr.io/taloan/dmoffice`, template <https://raw.githubusercontent.com/Taloan/dmOffice/master/unraid/dmoffice.xml>.
   - Setup: set and safeguard `DMOFFICE_MASTER_KEY`, the first visit creates the admin account, HTTPS through a reverse proxy for phones/DAV.
   - A note that bugs are welcome as GitHub issues, too.
5. Note the address of the thread (`https://forums.unraid.net/topic/<number>-…/`) and enter it:
   - `unraid/dmoffice.xml`: set `<Support>` to the thread (so far GitHub issues).
   - `ca_profile.xml`: add `<Forum>https://forums.unraid.net/topic/…</Forum>`.
   - Commit and push to GitHub.

## 3. Submit to CA

1. Open <https://ca.unraid.net/submit> (sign in with GitHub) → **Submit new** (<https://ca.unraid.net/submit/new>).
2. Enter the repository URL: `https://github.com/Taloan/dmOffice`.
3. Run **Validate**, fix reported errors in the XML files, push, check again.
4. Run **Scan**: exactly one app `dmOffice` must be found.
5. Submit. Docker apps are checked automatically or by the moderators; inclusion can take a few days.
6. Afterwards search for `dmOffice` under *Apps* in Unraid.

## Duties afterwards

- Answer questions in the support thread.
- Keep the image working with new Unraid versions.
- When you stop maintaining it, inform the CA moderators (or put `<Deprecated>true</Deprecated>` in the template).

## Publishing a new version

```bash
git tag v1.0.0
```

```bash
git push github v1.0.0
```

The workflow then builds `:1.0.0`, `:1.0` and `:latest`. Every push to `master` updates `:latest`.

Changes only under `apps/` or `docs/` do not build a new image.

## Publishing the Android app

For every tag `v…` (and `android-v…`), `.github/workflows/android.yml` builds the APK, signs it and attaches it to a GitHub release. Create a signing key once and keep it safe (without it there are no more updates):

```bash
keytool -genkeypair -v -keystore dmoffice.jks -alias dmoffice -keyalg RSA -keysize 4096 -validity 10000
```

Create under *Settings → Secrets and variables → Actions* in the repository: `ANDROID_KEYSTORE_BASE64` (contents of `base64 -w0 dmoffice.jks`), `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` (`dmoffice`), `ANDROID_KEY_PASSWORD`. Never put the file `dmoffice.jks` into the repository.
