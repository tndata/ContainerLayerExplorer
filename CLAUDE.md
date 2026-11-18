# Container Layer Explorer

A local web tool that shows the layers of a Docker/OCI image as a matrix: which files exist in which layer, and what each layer added, changed or deleted. A .NET 10 minimal API with a plain HTML, CSS and JavaScript front end. MIT licensed.

## Layout

- `src/ContainerLayerExplorer`: the web app. `Api/` (endpoints, error mapping, event stream), `Docker/` (Docker Engine API client over the named pipe or Unix socket), `Images/` (saving, unpacking, limits, cache folder), `wwwroot/` (`index.html` image list, `explorer.html` the Stack and Matrix views of one image, ES modules with `stack.js` for the Stack, `app.css`).
- `src/ContainerLayerExplorer.Core`: reads an unpacked image into the layer model. No web or Docker code.
- `tests/ContainerLayerExplorer.Tests`: xunit v3 tests. They need no Docker.

## Build and test

```bash
dotnet build
dotnet test
dotnet run --project src/ContainerLayerExplorer   # http://localhost:5000
```

## Conventions

- No framework on the front end and no NuGet packages in the app. Test packages are fine.
- Every behavior change gets a test. The build must stay at zero warnings.
- Plain English in code comments and docs. No em dashes.
- Settings that users may change live in `appsettings.json` and are bound to `Limits`.

## Limits of the tool

- Read-only: it never pushes, pulls, deletes or changes images and never starts containers.
- Localhost only. Host filtering rejects other host names. Never add a way to expose it on a network.
- Tested by hand with Docker Desktop on Windows. CI runs the unit tests on Linux only.
- Do not add Docker logos or imply any affiliation with Docker, Inc.
