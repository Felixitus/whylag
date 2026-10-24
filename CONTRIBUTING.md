# Contributing

Most valuable: **real cases where WhyLag was wrong** – open a *Wrong diagnosis* issue with the copied report.
Also welcome: rules, collectors, translations and tests on other hardware.

## Setup

```bash
git clone https://github.com/Felixitus/whylag.git
cd whylag
dotnet test WhyLag.slnx
dotnet run --project src/WhyLag.App
```

Windows 10/11 and the .NET 10 SDK. Ollama is not needed for tests.

## Pull requests

- One topic per PR; `dotnet build` without warnings, `dotnet test` green, `dotnet format` clean.
- New rules: tests for "fires" and "calm data does not fire"; texts in every language file.
- Rules report what the data shows – numbers go into evidence, summaries are for non-technical users, no claims of proof.
- No new network connections, telemetry or admin requirements without an issue first.
- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/).

Contributions are licensed under [Apache-2.0](LICENSE). Please follow the [Code of Conduct](CODE_OF_CONDUCT.md).
