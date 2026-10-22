# Contributing to dmOffice

Thank you for your interest! dmOffice is a self-hosted office suite (mail, calendar, contacts, tasks, notes, files) by a single maintainer, so please keep contributions focused and small.

**Languages:** issues and pull requests are welcome in English or German. The documentation is English first with a German version next to it (`xyz.md` and `xyz.de.md`).

## Reporting bugs and asking for features

- Search the [existing issues](https://github.com/Taloan/dmOffice/issues) first.
- Use the templates when you open a new issue. For bugs please include the dmOffice version (shown in the folder pane and under *Admin*), how you run it (Unraid, Docker Compose …), what you did, what you expected and what happened. Logs help (`docker logs dmoffice`); remove anything private.
- **Security problems:** please do not open a public issue. Contact the maintainer privately through the address on the GitHub profile.

## Building and testing

```bash
dotnet run --project DmOffice.Server      # server on http://localhost:5099
cd web && npm install && npm run dev      # web interface on http://localhost:5173
dotnet test                               # tests
```

The Android app is built from `apps/mobile` (JDK 17 and the Android SDK, see [apps/mobile/README.md](apps/mobile/README.md)). More details are in the [README](README.md#development).

## Pull requests

- One topic per pull request; describe what changes and why.
- Run `dotnet test` (and the app's unit tests if you touch the app) before you open it.
- The interface is bilingual: user-visible texts go through the translation files in `web/src/i18n/` and the string resources of the app (`values/` and `values-de/`). Add both languages.
- Documentation changes: keep the English and German versions in step where you can; if you only write one, say so in the pull request.
- Do not commit generated files, secrets or personal data. Screenshots in the documentation come from `tools/docs/make.sh` with made-up sample data.
- `vendor/Notizi.Core` is a copy from the notizi project; changes belong upstream.

## License

By contributing you agree that your contribution is licensed under the [GNU Affero General Public License, version 3](LICENSE), like the rest of dmOffice.
