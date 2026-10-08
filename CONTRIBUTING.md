# Contributing

Thanks for helping to make AI-Usage better. Bug reports, ideas and pull requests are all welcome.

## Before you start

- **Bugs and ideas:** open an issue with the matching template first, so we can agree on the
  approach before you spend time on code.
- **Security problems:** never in a public issue, see [SECURITY.md](SECURITY.md).

## Build and test

You need Windows 10 or 11 and the .NET 10 SDK.

```bat
dotnet build
dotnet test tests/AiUsage.Tests
```

`build.bat` runs the full pipeline (build, tests, x64 and ARM64 publish, installer). The installer
step needs Inno Setup 6.

## Guidelines

- **Read-only toward the agents.** AI-Usage reads usage numbers only. A change must never call a
  chat, completion or message endpoint, never write to another tool's files and never store its
  credentials. `TokenSafetyTests` guards the endpoint list.
- **Tests with every change.** New behaviour comes with a test; a bug fix comes with a test that
  failed before the fix.
- **Both languages.** Every user-facing text goes into `src/AiUsage/Resources/Strings.resx` (English)
  and `Strings.de.resx` (German). The German UI addresses the user informally ("du").
- **Match the code around you.** Formatting follows `.editorconfig`; the build treats warnings as
  errors. Keep comments short and explain why, not what.
- **One topic per pull request,** with a short description of what changed and how you tested it.
  Screenshots help for anything visible, in both the light and the dark theme.

## License

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).
