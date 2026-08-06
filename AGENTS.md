# Agent guidance

Read [DEVELOPMENT.md](DEVELOPMENT.md) before changing this repository; it is the authoritative guide to the architecture, generator constraints, tests, and packaging.

- Use a compatible .NET 10 or later SDK.
- Run `dotnet build` and `dotnet test` after code changes.
- Keep BSON wire-format logic in `MiniBson`; generated model-specific code belongs in `MiniBson.Generator`.
- Add byte-level coverage for wire-format changes because round-trip tests can hide matching reader and writer bugs.
