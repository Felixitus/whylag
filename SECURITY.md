# Security policy

## Supported versions

Only the latest release receives security fixes while WhyLag is in the 0.x phase.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private reporting instead:
**Security → Report a vulnerability** in this repository.

Include what you found, how to reproduce it and the affected version. You will get an answer within 7 days.
Fixes are published together with an advisory; reporters are credited unless they prefer otherwise.

## Scope

Relevant are, for example:

- data leaving the PC without the user's consent (including through the AI feature),
- incident files or exports revealing more than documented in [docs/privacy.md](docs/privacy.md),
- ways for other local users or programs to make WhyLag execute code or change system settings,
- the AI output influencing anything other than displayed text.

Problems in Ollama or in AI models themselves should be reported to those projects.
