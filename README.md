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
| `Shift+X` | export the build as a `.json` file (also per-save in the browser) |
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

## Levels that ship with the game

Saves live per player, and on the web per *origin* — a build served from a different port or host sees
an empty save list, because IndexedDB is scoped that way. So a creation meant to be part of the game
cannot live in the save store. Bundled levels are compiled in instead:

1. Build the level. In the editor, press `S` — that writes a real file. In a browser build, press
   `Shift+X` (or **Export** on a card in the save browser) to download the `.json`, since browser
   saves live in IndexedDB and have no file to pick
2. **Block Marble Run → Bundle a Saved Creation** — pick the save; it is copied into
   `Assets/Resources/Levels/` with its thumbnail
3. It appears in the save browser (`L`) for everyone, marked "comes with the game"

They are read-only, not by protection but by nature: there is nowhere in a build to write back to.
Opening one and saving puts a copy in the player's own store, which is what you want from an example.

The save self-test parses every bundled level and checks it names only parts this build has — a level
that ships naming a renamed part opens to a hole on a stranger's machine, with no way to tell them.

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

`.github/workflows/build.yml` builds the three configurations **on demand, on a self-hosted runner on
the author's Mac**. There are no licence secrets, because there is no licence to put in one: Unity has
retired manual activation for Personal licences — the portal accepts a Plus/Pro serial and nothing
else — and a Unity 6 Personal entitlement has neither a serial nor a `.ulf`. `game-ci/unity-builder`
runs the editor in Docker and cannot start without one, so the hosted path is closed rather than
merely unconfigured. The machine that already has an activated editor builds instead.

```bash
tools/ci-build.sh                # WebGL, no deploy
tools/ci-build.sh macos          # macOS
tools/ci-build.sh all deploy     # everything, and publish to Pages
```

The script brings the runner up, dispatches the workflow, streams the result, and takes the runner
down again on any exit — including a failed build or a Ctrl-C. The runner exists for the length of one
build and no longer: a machine sitting quietly available to run jobs is exactly what should not be
there on a public repository. It is registered to this repository only and is **not** a launchd
service.

Deploying publishes `build/webgl-pages` to the **`gh-pages` branch** as a fresh orphan commit, which
Pages serves at <https://anttikuosmanen-rgb.github.io/block-marble-run/>. That is the build carrying
the JavaScript decompressor, because Pages cannot send `Content-Encoding: br`.

**The workflow uses no actions at all, on purpose.** A self-hosted runner downloads every action's
tarball from `codeload.github.com` on every job — hosted runners have them baked into the image — and
an afternoon of builds was enough to get the machine's address 429ed, which failed jobs in setup
before Unity was reached. `git` does the same work. The visible cost is no uploaded artifacts: builds
stay in the runner's workspace, on the machine that made them.

**Do not add a `push` or `pull_request` trigger while this runs on the self-hosted runner.** The
repository is public, so anyone can fork it and open a pull request; a workflow that started
automatically would run their code on the author's Mac. `workflow_dispatch` can only be started by
someone with write access, which is what makes the arrangement safe. Automatic builds would have to
move to a hosted runner first — and that needs the licence problem above solved.
