# Block Marble Run

A Duplo-style marble run. Build a track out of real bricks — blocks, plates, slides, curves, funnels,
pillars — then switch to play mode, drop a marble in and watch it run. Unity 6.5, URP, targeting
macOS and WebGL.

The parts are 3-D-printable Duplo-compatible STLs at their real dimensions: 16 mm stud pitch, 19.2 mm
bricks. The grid the game snaps to is the same one the plastic uses, so anything that fits together in
the game fits together on the floor.

See [DESIGN.md](DESIGN.md) for the reasoning behind all of it — grid model, physics tuning, asset
pipeline, and a running record of what was measured and what turned out to be wrong.

## Status

Playable. Building, editing, saving, marbles, water and sound all work.

| | |
|---|---|
| **Built** | M0 import pipeline · M1 grid & placement · M2 editing · M3 track & supports · M4 play · M5 feel |
| **Not built** | M6 content — challenge mode, scoring, share codes |
| **Deliberately unbuilt** | Soft marble assist. Welding the collider seams between joined track pieces removed the problem it was designed to solve (DESIGN.md §7.1, §13) |
| **CI** | Disabled — see [below](#ci) |

## Building

Point at what you want to place and click. The piece rests on the highest thing under it, and snaps
to studs or to channel mouths — a channel joining another channel holds a run up exactly as a stud
holds an anti-stud.

Nothing has to be supported. Build a curve in mid-air and the game puts a pillar under it, cut to the
right height, undoable in the same step as the piece itself.

| | |
|---|---|
| Left click | place |
| Right click | pick the piece back up · drag to orbit |
| **Shift** | precise placement — slides stud by stud, never picks the ground, wheel changes level |
| `Q` `E` | previous / next part |
| `R` | rotate, or cycle the ways a piece can join |
| `C` | colour · `X` mark a start or goal · `Del` remove |
| `V` | grab mode — click picks, drag box-selects, `A` all, `R` turn, `M` mirror |
| `Cmd/Ctrl C` `V` | copy · paste (first click places the group, second commits it) |
| `+` `-` | raise or lower a structure |
| `S` `L` | save · saved creations |
| `O` | restore the autosave (the build is kept automatically as you work) |
| `F` `Home` | frame the build · return to origin |
| `B` | floor: grid / sand / water |
| `Tab` | play mode |

## Playing

| | |
|---|---|
| `Space` | release from every start |
| Left click | drop a ball anywhere |
| `M` | change ball — plastic, glass, steel, wood, hollow, small glass |
| `C` | view: orbit / follow / chase / ride |
| `N` · right click a ball | watch the next ball · watch that one |
| `R` `Tab` | reset · back to building |
| `P` | physics read-out |

Balls differ by density, not by a hand-tuned mass, so a steel ball sinks and a plastic one very nearly
floats. Water depth is adjustable and is saved with the creation.

## Requirements

- Unity **6000.5.8f1**
- Build modules: **WebGL Build Support**, **Mac Build Support (IL2CPP)**

IL2CPP is required for a shippable macOS build. Without it, set `BMR_SCRIPTING_BACKEND=mono` for a
Mono smoke test — useful locally, not shippable.

First open: **Block Marble Run → Setup Project**. It creates the URP assets and applies the physics
tuning from DESIGN.md §2, including the 10× world scale and the matching gravity that keeps a 24.5 mm
marble inside PhysX's usable range. Idempotent.

## Layout

```
Assets/Art/Meshes/          26 source .stl parts — the source of truth
Assets/Parts/Definitions/   37 PartDefinitions, mostly machine-derived
Assets/Parts/Marbles/       ball types — size and density, mass derived from them
Assets/Scripts/             runtime code (BlockMarbleRun.Runtime)
Assets/Editor/Import/       STL/OBJ import, part analysis, mirror and plate generation
Assets/Editor/Tests/        self-tests and headless probes
Assets/Settings/            URP pipeline assets
PartMasks.txt               every part's studs and anti-studs as ASCII, for checking by eye
```

## Adding a part

Drop a `.stl` into `Assets/Art/Meshes/`. The importer converts it on the spot — millimetres to world
units, CAD Z-up to Unity Y-up, and welds the triangle soup into shared vertices so curves shade
smoothly. Then:

1. **Block Marble Run → Report Parts** — footprint, layers, studs and mirror verdict for every part.
   Read it before generating anything.
2. **Block Marble Run → Generate Part Definitions** — creates or refreshes a `PartDefinition` per
   part, generates mirror meshes for chiral parts and plate variants for blocks.
3. **Block Marble Run → Write Part Mask Report** — rewrites `PartMasks.txt`. **Look at it.** Studs and
   anti-studs are derived from the geometry, and reading thirty-seven small ASCII diagrams has caught
   errors that no amount of looking at the 3-D view did.
4. Fill in what the analyser cannot know: category and display name.

Re-running step 2 is safe: it rewrites derived fields only, so authored data and reviewed mirror
verdicts survive.

A Lego-scale OBJ can join the set through **Block Marble Run → Convert OBJ to Duplo STL**, which
writes an STL the normal pipeline then reads.

### Mirrors need one human decision

A part is only worth mirroring if its mirror *cannot* be reproduced by a 90° rotation the game already
offers — otherwise the palette quietly gains a duplicate, which is a bug nobody reports; they just find
the palette confusing. The analyser compares mirrored *volume* (not vertices, which measures
tessellation) and classifies each part `Redundant` / `Chiral` / `Ambiguous`. Anything ambiguous wants a
human verdict on `mirrorVerdict`, and the verdict is then remembered. **Reanalyse Mirrors** re-derives
every verdict and deletes mirrors that are no longer justified. See DESIGN.md §3.4.

## Checks

```
Block Marble Run → Run Grid Self Test     placement, rotation, masks, scaffolding
Block Marble Run → Run Save Self Test     save round-trip and the v1→v3 migrations
```

Both run headless in batch mode too, which is how they are usually run.

## Builds

```bash
UNITY=/Applications/Unity/Hub/Editor/6000.5.8f1/Unity.app/Contents/MacOS/Unity

$UNITY -projectPath . -batchmode -quit -nographics \
  -executeMethod BlockMarbleRun.EditorTools.Bootstrap.BuildScript.BuildWebGLPages
```

Entry points: `BuildWebGLSelfHost`, `BuildWebGLPages`, `BuildMacOS`. Output goes to `build/`.

There are two WebGL configs because the hosts differ, not out of preference. GitHub Pages cannot set
`Content-Encoding: br`, so it needs the fallback decompressor; a self-hosted server sends the header
and ships without one. See DESIGN.md §9.1.

### CI

`.github/workflows/build.yml` builds all three targets and deploys the Pages build. **Its automatic
triggers are off**: `game-ci/unity-builder` runs the editor headless in Docker and needs a Unity
licence in the repository secrets, and without one every push failed in under twenty seconds — a red
mark on each commit that says nothing about the commit.

To turn it back on, add three repository secrets and restore the `push` and `pull_request` triggers,
which are kept in a comment at the top of the workflow:

```bash
# 1. produce an activation request from the installed editor
/Applications/Unity/Hub/Editor/6000.5.8f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -nographics -quit -createManualActivationFile -logFile -
# 2. upload the resulting Unity_v6000.5.8f1.alf at https://license.unity3d.com/manual,
#    pick Unity Personal, and download the .ulf it returns
# 3. hand it to GitHub
gh secret set UNITY_LICENSE < Unity_v6000.5.8f1.ulf
gh secret set UNITY_EMAIL       # the Unity account address
gh secret set UNITY_PASSWORD    # its password
```

GameCI's old `unity-request-activation-file` action is retired and answers any run with "this action
is no longer supported", so the `.alf` comes from the local editor now. Pages deployment also needs
Pages enabled with **GitHub Actions** as the source.

Note that a Personal licence is single-seat: activating it in CI can knock the local editor's
activation loose. GameCI has a return-licence step for that.
