# Block Marble Run

A Duplo-style marble run: build a track out of bricks, then release a marble and watch it run.
Unity 6.5, URP, targeting macOS and WebGL.

See [DESIGN.md](DESIGN.md) for the full design — grid model, physics tuning, asset pipeline,
milestones and the reasoning behind each decision.

## Status

**M0 complete** — project, import pipeline, part analysis, build configs. Nothing is playable yet;
M1 adds placement.

## Requirements

- Unity **6000.5.8f1**
- Build modules: **WebGL Build Support**, **Mac Build Support (IL2CPP)**

The IL2CPP module is required to produce a shippable macOS build. Without it, local macOS builds
fail — `BMR_SCRIPTING_BACKEND=mono` runs a Mono smoke test instead, which is not shippable.

## Layout

```
Assets/Art/Meshes/          20 source .stl parts — the source of truth
Assets/Art/Meshes/Generated/ auto-generated mirror meshes (do not hand-edit)
Assets/Parts/Definitions/   one PartDefinition per part, mostly machine-derived
Assets/Scripts/             runtime code (BlockMarbleRun.Runtime)
Assets/Editor/              import pipeline and tooling (BlockMarbleRun.Editor)
Assets/Settings/            URP pipeline assets
```

## Adding a part

Drop a `.stl` into `Assets/Art/Meshes/`. The importer converts it on the spot — millimetres to world
units, CAD Z-up to Unity Y-up, and welds the triangle soup into shared vertices so curves shade
smoothly. Then:

1. **Block Marble Run → Report Parts** — prints footprint, layer count, studs and mirror verdict for
   every part. Read it before generating anything.
2. **Block Marble Run → Generate Part Definitions** — creates or refreshes a `PartDefinition` per
   part and generates mirror meshes for chiral parts.
3. Fill in what the analyser cannot know: category, track ports, centreline.

Re-running step 2 is safe. It only rewrites derived fields, so authored ports, centrelines and
reviewed mirror verdicts survive.

### Mirrors need one human decision

A part is only worth mirroring if its mirror *cannot* be reproduced by a 90° rotation the game
already offers. The analyser scores that and classifies each part `Redundant` / `Chiral` /
`Ambiguous`. Currently two parts land in the middle and need a human verdict:

| Part | Score | Why it's ambiguous |
|---|---|---|
| `bridge_2x3` | 0.92 | Near-symmetric; mirror is probably just a rotation |
| `curve_2x2` | 0.90 | Mirror *should* be a pure rotation, but asymmetric detail drags the score down |

Set `mirrorVerdict` on those two by hand; the choice is then remembered. Auto-deciding would
silently ship a duplicate `curve_2x2` — a palette offering the same piece twice is a bug nobody
reports, they just find the palette confusing. See DESIGN.md §3.4.

## Builds

```bash
UNITY=/Applications/Unity/Hub/Editor/6000.5.8f1/Unity.app/Contents/MacOS/Unity

$UNITY -projectPath . -batchmode -quit -nographics \
  -executeMethod BlockMarbleRun.EditorTools.Bootstrap.BuildScript.BuildWebGLPages
```

Entry points: `BuildWebGLSelfHost`, `BuildWebGLPages`, `BuildMacOS`.

There are two WebGL configs because the hosts differ, not out of preference. GitHub Pages cannot set
`Content-Encoding: br`, so it needs the fallback decompressor; a self-hosted server can send the
header and ships without it. See DESIGN.md §9.1.

CI builds all three on every push and deploys the Pages build from `main`. Keeping WebGL green from
day one is deliberate — WebGL failures (heap exhaustion, stripping breaking serialization) surface
late and can force architectural rework.

### CI setup

The workflow needs Unity credentials as repository secrets: `UNITY_LICENSE`, `UNITY_EMAIL`,
`UNITY_PASSWORD`. See [game.ci](https://game.ci/docs/github/activation) for obtaining a licence file.
Pages deployment also needs Pages enabled with **GitHub Actions** as the source.

## Project setup

`Block Marble Run → Setup Project` (or the `SetupProject.Run` method) creates the URP assets and
applies the physics tuning from DESIGN.md §2 — including the 10× world scale and matching gravity
that keep a 13 mm marble inside PhysX's usable range. It is idempotent.
