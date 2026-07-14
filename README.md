# Prime Rust Extractor

The next-generation asset extractor for **Rust** by Facepunch Studios — built to answer one question well: *"I want the hot air balloon, with its textures, materials, and animations — as one file."*

## Best of all worlds

This project deliberately combines three lineages:

| From | We take |
|---|---|
| **[AssetRipper](https://github.com/AssetRipper/AssetRipper)** (engine, via submodule + NuGet) | version-proof Unity parsing — every class generated from Unity's own type layouts (3.5 → Unity 6+), prefab hierarchy reconstruction, GLB/glTF model export, managed texture decoding |
| **[Rust Asset Studio](https://github.com/CalvFletch/Rust-Asset-Studio)** (our sibling project) | the Rust layer: Steam install auto-detection, GameManifest + ItemDefinition knowledge, the player-vocabulary object catalog design |
| **RAE** (architecture lessons) | build-ID-keyed persistent caching, lazy loading, UI-before-data, template-instancing exports |

**Rust Asset Studio remains maintained** as the familiar AssetStudio-style browser for everyday use. Prime Rust Extractor is the ground-up rebuild aimed at the player-vocabulary workflow: search "hot air balloon", get a complete, correctly-textured export.

## Status

Engine spike passing: AssetRipper's engine (pinned at `1.2.5`, .NET 9) loads a live Rust Unity 6 bundle through our CLI — 10,674 assets parsed in 0.5s, prefab hierarchies reconstructed automatically.

Next milestones:

1. **Object catalog** — parse `GameManifest` + `ItemDefinition`s into a build-ID-keyed index: display name ("Hot Air Balloon") → prefab path → assets
2. **`pre find <query>`** — search the catalog from the CLI
3. **`pre export <object> --glb`** — complete-object export with textures and materials, animations on request
4. UI (likely local web, following AssetRipper's precedent) once the CLI workflow is proven

## Building

```
git clone --recursive https://github.com/CalvFletch/Prime-Rust-Extractor.git
dotnet build PrimeRustExtractor.sln -c Release
dotnet run --project PrimeRustExtractor.Cli -- "<path-to-rust>\Bundles\shared\items.preload.bundle"
```

Requires the .NET 9 SDK. The `--recursive` matters: the AssetRipper engine is a git submodule pinned to a known-good tag, and `dev_data/server` references the community-maintained decompiled Rust server source ([Zaddish/rust-changes](https://github.com/Zaddish/rust-changes)) used as schema documentation for game structures (LOD systems, manifests, item definitions).

## License

[GPL-3.0-or-later](LICENSE.md) — inherited from the AssetRipper engine this project builds on. Not affiliated with Facepunch Studios; use only with game files you legitimately own.
