# Contributing

Thanks for your interest in improving **Scheduled Command Executor**! Contributions of all sizes are welcome — bug reports, docs, and pull requests.

## Getting started

```powershell
git clone https://github.com/peopleworks/RunCommandsService.git
cd RunCommandsService
dotnet build -c Debug
dotnet run --project .\RunCommandsService.csproj
```

The dashboard is then available at <http://localhost:5058/>.

## Ways to contribute

- **Report a bug** — open an issue using the *Bug report* template. Include your OS, .NET version, the relevant `appsettings.json` (with secrets removed), and logs.
- **Suggest a feature** — open an issue using the *Feature request* template and describe the use case.
- **Send a pull request** — see below.

## Pull requests

1. Fork the repository and create a feature branch (`feature/short-description`).
2. Keep changes focused; one logical change per PR.
3. Make sure it builds cleanly: `dotnet build -c Release`.
4. Match the existing code style (nullable enabled, implicit usings, 4-space indent).
5. Update the README / changelog when behavior or configuration changes.
6. Open the PR against `master` and fill in the PR template.

## Reporting security issues

Please **do not** open a public issue for security vulnerabilities. Instead, contact the maintainer privately so the issue can be addressed before disclosure.

## Code of conduct

Be respectful and constructive. We want this to be a welcoming project for everyone.
