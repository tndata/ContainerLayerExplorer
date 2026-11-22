# Contributing

Container Layer Explorer is a small teaching tool. Bug reports and small fixes are welcome.

## Before you start

- For a bug, open an issue with the steps to reproduce it.
- For a larger change or a new feature, open an issue first so we can agree on it before you spend time on code. The tool is deliberately small, so I may say no.
- I cannot promise how fast I answer.

## Build and test

You need the .NET 10 SDK and, to try the tool for real, Docker Desktop.

```bash
dotnet build
dotnet test
dotnet run --project src/ContainerLayerExplorer
```

When the terminal shows `Now listening on: http://localhost:5000`, open that address in your browser.

The tests do not need Docker. They run on Linux in GitHub Actions. The tool itself is only tested by hand with Docker Desktop on Windows, so say in your pull request what you tried it with.

## Pull requests

- Keep each pull request small and about one thing.
- Add or update a test for every change in behavior.
- Keep the build free of warnings.
- The web page uses plain HTML, CSS and JavaScript, and the app has no NuGet packages. Please do not add a framework or a package without talking to me first.

There are no binary releases. The tool is built from source.

## Conduct

Be kind. We follow the [Contributor Covenant](.github/CODE_OF_CONDUCT.md).
