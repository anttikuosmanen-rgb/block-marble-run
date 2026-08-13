# Block Marble Run — Design & Implementation Plan

Unity 6.5 (6000.5.x), URP, PhysX (built-in 3D physics), Input System, UI Toolkit.

Two modes: **Build** (Duplo-style brick placement) and **Play** (marble released, rigidbody physics).

**Targets: macOS (Apple Silicon, Metal) and WebGL.** World is an open universe — no bounded baseplate.

---

## 0. Platform targets

macOS is the easy target. **WebGL is the binding constraint and dictates several decisions below**; design to WebGL and macOS comes free.

### 0.1 WebGL constraints that shape the architecture

| Constraint | Consequence |
|---|---|
| **Single-threaded** (no pthreads without SharedArrayBuffer + COOP/COEP headers) | PhysX runs on the main thread with no job workers. Budget accordingly: static colliders are cheap, *dynamic bodies* are the cost. Cap concurrent marbles (~16 default, hard limit ~32). |
| **No compute shaders** on WebGL2 | `RenderMeshIndirect` is out. `RenderMeshInstanced` / GPU instancing works — that's the scaling path (§5). |
| **Constrained heap**, no virtual memory | Weld STL verts (§3.1) — 3× vert reduction is a memory win, not just a visual one. `Mesh.UploadMeshData(markNoLongerReadable: true)` on all render meshes. Fallback `MeshCollider` meshes must stay readable, so prefer primitive colliders (§3.3) for that reason too. |
| **No real filesystem** — `persistentDataPath` is emscripten IDBFS, async-flushed | Save/load goes behind an `ISaveStore` interface (§8). Never assume a write is durable on return. |
| **Downloads/uploads need browser APIs** | `.jslib` plugin for Blob download + `<input type="file">` import, for sharing creations. |
| **Audio needs a user gesture**, WebAudio backend only | No audio filters/DSP. Start the audio context on first click. Cap simultaneous voices (marble clacks are the risk). |
| **Long startup, big binary** | IL2CPP + high code stripping, Brotli compression, disable decompression fallback (requires server `Content-Encoding` headers). Strip unused engine modules. |
| **Pointer lock / fullscreen need a gesture** | Camera control must work without pointer lock. Orbit-on-drag, not FPS-style capture. |

**Renderer**: URP Forward, WebGL2 baseline. WebGPU is available in Unity 6 but treat it as an opt-in fast path, not the target — ship on WebGL2. One realtime directional light, no baked GI (an open universe with user-built geometry can't be baked anyway), soft shadows off or low-res cascade.

**Input**: both targets are desktop browsers/desktop OS → mouse + keyboard is the single primary scheme. Touch is *not* a target; keep it out of M1–M5 scope. Input System actions stay abstract so tablet Safari can be added later without rework.

---

## 1. Measured asset metrics

Bounding boxes computed from the shipped binary STLs (mm, Z-up, origin at bottom-center):

| STL | X | Y | Z | footprint | layers | top studs | tris |
|---|---|---|---|---|---|---|---|
| `building_block_1x2` | 31.8 | 15.8 | 23.8 | 2×1 | 1 | yes | 4520 |
| `building_block_2x2` | 31.8 | 31.8 | 23.8 | 2×2 | 1 | yes | 8328 |
| `building_block_2x3` | 31.8 | 47.8 | 23.8 | 2×3 | 1 | yes | 11360 |
| `building_block_2x6` | 31.8 | 95.8 | 23.8 | 2×6 | 1 | yes | 16852 |
| `building_block_2x8` | 31.8 | 127.8 | 23.8 | 2×8 | 1 | yes | 19980 |
| `building_block_2x10` | 31.8 | 159.8 | 23.8 | 2×10 | 1 | yes | 22356 |
| `bridge_2x3` | 31.8 | 47.8 | 23.8 | 2×3 | 1 | yes | 10216 |
| `track_2x2` | 31.8 | 31.8 | 19.2 | 2×2 | 1 | no | 2164 |
| `track_2x4` | 31.8 | 63.8 | 19.2 | 2×4 | 1 | no | 3078 |
| `track_2x6` | 31.8 | 95.8 | 19.2 | 2×6 | 1 | no | 4032 |
| `track_2x8` | 31.8 | 127.8 | 19.2 | 2×8 | 1 | no | 4778 |
| `curve_2x2` | 31.8 | 31.8 | 19.2 | 2×2 | 1 | no | 6632 |
| `curve_4x4` | 63.8 | 63.8 | 19.2 | 4×4 | 1 | no | 8184 |
| `crossing_2x2` | 31.8 | 31.8 | 19.2 | 2×2 | 1 | no | 2104 |
| `terminal_2x2` | 31.8 | 31.8 | 19.2 | 2×2 | 1 | no | 4292 |
| `slide_2x2` | 31.8 | 31.8 | **38.4** | 2×2 | **2** | no | 14338 |
| `slide_2x4` | 31.8 | 63.8 | **38.4** | 2×4 | **2** | no | 16968 |
| `slide_curve_4x4` | 63.8 | 63.8 | **38.4** | 4×4 | **2** | no | 10114 |
| `u_turn` | **78.2** | 95.8 | 23.8 | 5×6 | 1 | yes | 13492 |
| `u_turn_slide` | 95.8 | **78.2** | **43.0** | 6×5 | **2** | yes | 20922 |

Every Z value decomposes as `layers × 19.2 (+ 4.6 if top studs)`: 19.2, 23.8, 38.4, 43.0. The model holds across all 20 parts — no new height machinery needed, just `heightLayers > 1`.

Derived constants (real Duplo geometry — assets match it exactly):

```
STUD_PITCH_MM   = 16.0      // XY grid
LAYER_HEIGHT_MM = 19.2      // one brick tall = 1.2 * pitch
STUD_HEIGHT_MM  = 4.6       // 23.8 - 19.2
CLEARANCE_MM    = 0.2       // n*16 - 0.2 per axis
```

Track parts are exactly 1 layer tall with no top studs → **uniform single-layer grid, no special cases**.

### 1.1 Bounding boxes are not footprints

`u_turn` is **78.2 mm** across — but `5 × 16 − 0.2 = 79.8`. It is 1.6 mm (one wall thickness) short of its own footprint, because the outer arc wall doesn't reach the grid edge. A naive `round(bounds / 16)` gives 4.89 → 5 by luck, but the tolerance check would fire a false alarm, and a future part inset by 8 mm would round to the *wrong* footprint outright.

So footprint derivation is two-source (§3.2): bounding box gives a candidate, **underside socket geometry** confirms it. Where they disagree, the importer flags the part for human confirmation rather than guessing.

### 1.2 Pivot / parity gotcha

`1x2` spans 2 studs in X (center falls on a stud *boundary*) and 1 stud in Y (center falls on a stud *center*). Mixing parities breaks naive integer snapping.

Fix: work internally in **half-stud units (8 mm)**. A part occupying cells `[minCell, minCell+size)` sits at
`worldXY = (minCell + size * 0.5) * STUD_PITCH`. All parities collapse to one formula.

---

## 2. World scale & physics tuning

A 13 mm marble in a 32 mm trough is far below PhysX's comfortable range — `defaultContactOffset` alone is 10 mm by default, i.e. bigger than the marble radius. Two fixes; **use both**:

**Scale the world 10×** (1 Unity unit = 10 cm real):

```
STL import scale   = 0.01          // brick = 0.318 units, marble = 0.13 units
Physics.gravity    = (0, -98.1, 0) // length scaled by k=10 ⇒ gravity × k keeps
                                   // real-time dynamics identical to a real marble
```

**Tighten solver settings** (`ProjectSettings/Physics` + bootstrap):

```
Time.fixedDeltaTime            = 1/120        (drop to 1/200 if tunneling persists)
Physics.defaultContactOffset   = 0.002
Physics.defaultSolverIterations = 10
Physics.defaultSolverVelocityIterations = 4
Physics.bounceThreshold        = 1.0
```

Marble rigidbody:

```
collisionDetectionMode = ContinuousDynamic
maxAngularVelocity     = 200      // CRITICAL: default 7 rad/s hard-clamps a small
                                  // rolling sphere and it "sticks"/slides instead of rolls
interpolation          = Interpolate
mass ≈ 0.005 kg, angularDamping ≈ 0.02
```

Physics material (marble & track): `dynamicFriction 0.08`, `staticFriction 0.10`, `bounciness 0.15`, combine `Multiply`.

**WebGL budget.** 120 Hz on a single thread is affordable *because the track is static* — PhysX broadphase over sleeping static colliders is nearly free, and only the marbles are dynamic. The cost scales with concurrent marbles, not with build size. Guards:

```
Time.maximumDeltaTime = 0.1        // clamp the death spiral on a tab-switch/GC stall
maxConcurrentMarbles  = 16         // soft default; hard cap 32, oldest despawns
```

Profile on WebGL early (M4), not at the end. If 120 Hz doesn't hold, drop to 90 Hz *before* trading away `ContinuousDynamic` — losing CCD costs tunnelling, which is unshippable.

Note: PhysX is **not** cross-platform deterministic — and macOS vs. WebGL will differ. Do not build features that assume identical replays; record transforms if a replay/ghost is ever needed.

---

## 3. Asset pipeline (STL → part)

The STL set will grow, so the pipeline must be drop-in, not manual per-part work.

### 3.1 `ScriptedImporter` for `.stl` (Editor)

Keeps STL as source of truth. Responsibilities:

1. Parse binary STL (ASCII fallback).
2. Apply `0.01` scale and Z-up → Y-up rotation (rotate −90° about X) so the asset is authored-correct on disk, not fixed up per-prefab.
3. **Weld vertices** with a 30° smoothing angle — STL is a soup of unshared triangles; without welding, cylinders and curves render faceted and vertex count is 3× what it needs to be.
4. No UVs needed — Duplo look is flat solid color. Material takes color per-instance.
5. Emit a decimated `_collision` submesh (optional, see 3.3).

### 3.2 Auto-derived `PartDefinition`

Editor tool that generates a `PartDefinition` ScriptableObject from a mesh:

- **Footprint**: `ceil((bounds.size + 0.2) / 16mm)` per axis as a *candidate*, cross-checked against underside socket/tube clusters (§1.1). Agreement → accept silently. Disagreement → flag for confirmation.
- **Top studs**: cluster geometry above the `heightLayers × 19.2` plane by XY; each cluster centroid → a stud cell. Gives the connection mask for free, and distinguishes `bridge_2x3`/`u_turn` (studded) from track parts (not).
- **Height in layers**: `round(bodyHeight / 19.2)` where `bodyHeight` = total minus 4.6 if studs were detected. Verified against the four observed values (19.2 / 23.8 / 38.4 / 43.0).
- **Mirror**: chirality analysis, §3.4.
- Manual fields left to the author: category, display name, **track ports**, collider prefab override.

A `PartValidatorWindow` renders the mesh with a stud-grid overlay so mistakes are visible before the part reaches the palette. **The importer proposes, the author confirms** — see §3.4 for why full automation is the wrong goal here.

### 3.3 Colliders — do **not** use the render mesh

11 k-tri non-convex mesh colliders × hundreds of placed parts will not hold up, and a marble trough is concave so convex hulls are wrong.

Strategy per category:

- **Building blocks**: single `BoxCollider` (body only; studs are visual — the ball never rides on them meaningfully). Near-free.
- **Straight track**: compound of 3–4 `BoxCollider`s forming the V/U channel (floor + two angled walls).
- **Curves**: compound of N box segments around the arc (N ≈ 8 for 2×2, 16 for 4×4), authored once per part type as a prefab.
- **Fallback**: decimated non-convex `MeshCollider` (static-only) for parts too organic to approximate.

Colliders live in `Parts/Colliders/*.prefab`, referenced by `PartDefinition`.

### 3.4 Auto-generated mirror parts

Chiral parts need a left- and right-handed version. Authoring both in CAD doubles the modelling work and lets the two drift apart, so **the mirror is generated, never authored**.

**The generation itself is mechanical**, given a source part and a mirror axis:

```
mesh:      negate X on every vertex; negate X on normals;
           REVERSE TRIANGLE WINDING (else the part renders inside-out)
footprint: mirror the mask columns
topStuds / bottomSockets: mirror the masks
ports:     mirror cell, and flip facing E<->W (for an X mirror)
collider:  mirror the prefab's local positions and rotations
id:        "<sourceId>_mirror"   — stable, so saves survive
```

The mirror is a derived asset regenerated on import, not a checked-in duplicate. `PartDefinition.mirrorOf` points back at the source; the palette shows the pair side by side.

**Which parts need one is the hard part — and it is not "the asymmetric ones."** A quarter-turn curve is asymmetric, but its mirror is identical to a 90° yaw rotation, which the game already supports for free. Generating a mirror there just adds a duplicate palette entry. The real test is:

> Is the mirrored mesh reproducible by any of the four yaw rotations?
> Yes → **redundant**, skip. No → **chiral**, generate.

Running that test over the current 20 parts (vertex-set overlap, mirror × each of 4 rotations, best score):

| Part | best match | verdict |
|---|---|---|
| `track_2x2`, `crossing_2x2`, `u_turn` | 1.00 | redundant — mirror *is* a rotation |
| `bridge_2x3` | 0.92 | redundant |
| **`curve_2x2`** | **0.90** | **ambiguous — right on the threshold** |
| `slide_2x4` | 0.50 | chiral → generate |
| `terminal_2x2` | 0.22 | chiral → generate |
| `slide_2x2` | 0.23 | chiral → generate |
| `curve_4x4` | 0.12 | chiral → generate |
| `u_turn_slide` | 0.03 | chiral → generate |
| `slide_curve_4x4` | 0.01 | chiral → generate |

The scores are strongly bimodal — except `curve_2x2` at 0.90, sitting exactly on any threshold you'd pick. Geometrically its mirror *should* be a pure rotation; it scores 0.90 rather than 1.00 because of asymmetric detail (stud/rib placement), not because of the arc.

That single case is the design conclusion: **the detector proposes, the author confirms.** The importer classifies each part `Redundant` / `Chiral` / `Ambiguous`, and `PartValidatorWindow` shows the source and mirrored meshes side by side with the score, defaulting to the detector's guess. Fully automatic mirroring would silently ship a duplicate `curve_2x2` — a palette that offers the same piece twice is a worse bug than a one-click confirmation step, because nobody reports it, they just find the palette confusing.

Store the confirmed verdict in the `PartDefinition` so re-imports don't re-ask.

---

## 4. Core data model

```csharp
struct GridCoord { int x, y, layer; }              // studs, studs, brick-layers

class PartDefinition : ScriptableObject {
    string       id;               // stable GUID string — save files reference this, never an index
    string       displayName;
    PartCategory category;         // Block | TrackStraight | TrackCurve | Ramp | Start | Goal | Special
    Mesh         mesh;
    GameObject   colliderPrefab;
    Vector2Int   footprintSize;    // in studs
    bool[]       footprintMask;    // supports L-shapes / non-rect parts
    int          heightLayers;
    bool[]       topStuds;         // which cells expose a stud
    bool[]       bottomSockets;
    TrackPort[]  ports;            // track connectivity, see §6
    Vector3[]    centerline;       // rough channel path, for soft assist (§13.1);
                                   // empty = no assist on this part, pure physics
    string       mirrorOf;         // set on generated mirrors (§3.4)
    RotationMode rotation;         // Free90 | Half180 | None
}

class PlacedPart {
    PartDefinition def;
    GridCoord      origin;         // min-corner cell
    int            rot;            // 0..3, yaw × 90°
    byte           colorIndex;
    int            instanceId;
}
```

**Occupancy**: `Dictionary<GridCoord, PlacedPart>` — sparse hash, one entry per occupied cell. O(1) collision test, no fixed world bound, trivially serializable.

`GridMap` API: `TryPlace`, `Remove`, `QueryCell`, `QueryRegion`, `CellsFor(def, origin, rot)`.

### 4.1 Placement rules

1. Every cell of the rotated footprint, across all `heightLayers`, must be free.
2. **Support**: `layer == 0` **or** at least one bottom-socket cell rests on a cell whose part exposes a top stud. Matches real Duplo and blocks floating builds.
3. Track parts have no top studs → nothing stacks on track (correct: real Duplo track pieces are terminal). Bridge parts, when added, get `topStuds` set.

There is **no bounds check** — see §4.2.

### 4.2 Open universe

`layer == 0` behaves as an infinite baseplate: any part may be placed anywhere on the ground plane, and the world extends without limit in X/Y. The sparse `Dictionary<GridCoord, PlacedPart>` is already unbounded, so this is a *removal* of constraints, not new machinery.

**Ground is math, not a collider.** Do not fake an infinite baseplate with a giant `BoxCollider` — it costs precision and forces an arbitrary edge. Instead:

```csharp
// Ground: analytic, exactly infinite, no collider, no cost
bool hitGround = new Plane(Vector3.up, 0f).Raycast(ray, out float groundDist);
// Parts: normal physics raycast against placed geometry
bool hitPart   = Physics.Raycast(ray, out RaycastHit partHit, maxDist, partLayerMask);
// Nearest wins → target GridCoord
```

**Float precision.** At 1 unit = 10 cm, one stud = 0.16 units, so float32 stays comfortable to roughly ±10⁴ units — about 60,000 studs out. Not a practical limit. `GridCoord` uses `int`, so the *data* is unbounded regardless. No floating-origin machinery needed; explicitly out of scope.

**Soft home bound.** Unlimited space is disorienting, not liberating. Mitigations, all cosmetic — none block placement:

- Origin marker at `(0,0)`.
- **Frame build** (`F`) — fit camera to the occupied-cell AABB, tracked incrementally by `GridMap`.
- **Return to origin** button.
- Gentle nudge in the HUD past ~512 studs from origin ("far from home").

**Ground visuals**, cheapest → nicest, in this order:

1. **Procedural infinite grid** (default): one large ground quad, URP Shader Graph / hand-written shader drawing stud dots + 16 mm-pitch lines in *world space*, `fwidth`-based antialiasing, distance fade to kill moiré, fully fogged out at the horizon. Constant cost, no geometry, WebGL2-safe.
2. **Optional rendered baseplate** (toggle): a real studded baseplate mesh of finite size — 48×48 studs suggested — centred on origin, purely decorative and non-colliding. Gives the toy-box look when the build is small. Off by default for large builds.

Both are view-only. Placement validity never depends on which is shown.

**Chunking.** Group parts into 16×16-stud chunks (`Dictionary<Vector2Int, Chunk>`) for frustum-culling granularity and for the Play-mode mesh combine (§7). Unity culls per-renderer already, so this is a Play-mode/instancing optimisation, not required for M1.

Rotation of the footprint mask is a pure function in grid space; no transforms needed for validation.

---

## 5. Build mode

**Camera** — `OrbitCamera`: orbit / pan / dolly around a focus point, clamped pitch, zoom-to-cursor, `F` to frame the build. Drag-to-orbit — **no pointer lock**, since WebGL requires a gesture to grant it (§0.1). Driven by an Input System action map.

**Placement loop**

1. Raycast cursor → nearest of {top face of an existing part, ground plane at y=0} (§4.2).
2. Convert hit point → `GridCoord` (round to half-stud, snap to `hitLayer + 1` on a part top, `0` on the ground plane).
3. Render **ghost** part at that coord: translucent, green if `GridMap` validates, red otherwise, tinted via `MaterialPropertyBlock`.
4. Click / tap → issue a `PlacePartCommand`.

**Operations**: place, delete (pick + delete), move, duplicate, paint color, box multi-select, copy/paste a region, clear all.

**Undo/redo** — command pattern, mandatory from day one because retrofitting it is painful:

```csharp
interface IEditCommand { void Do(GridMap m); void Undo(GridMap m); }
// PlaceParts | RemoveParts | MoveParts | PaintParts  — all batch-capable
```

`CommandStack` bounded at ~200 entries.

**Support cascade**: deleting a part can orphan parts above it. Orphans **stand** — never deleted, never dropped. They are highlighted in build mode and get scaffolding generated for them on the switch to Play (§5.1). This makes build mode fully permissive: you can start a track in mid-air and let the game work out how to hold it up.

**Rendering budget**: start with one GameObject per part (MeshRenderer + SRP Batcher + GPU instancing on the material). Comfortable to a few thousand parts on macOS; WebGL2's higher per-draw-call cost brings that down, so the `RenderMeshInstanced` path batched by (mesh, color) — keeping collider GameObjects, which the data model already separates — is a *likely* need on WebGL rather than a hypothetical one. Measure at M1 with a 2000-part stress scene.

---

### 5.1 Auto-generated supports

Floating parts are not an error state — they are a *normal build style*. Requiring a player to hand-build a pillar under every raised curve is exactly the tedium that kills a toy. So: **build freely in mid-air; on the switch to Play, the game generates the scaffolding.**

**Ground-connectivity pass** (run on every `GridMap` mutation, incremental):

```
groundSet = BFS upward from all layer-0 parts, following stud-support edges
orphans   = allParts \ groundSet
```

**Scaffold generation** (on Build → Play):

```
for each orphan, in ascending layer order:
    if now ground-connected (an earlier scaffold reached it): skip
    pick anchor cells from its footprint mask:
        span <= 2 studs  -> 1 cell  (centroid, snapped into the mask)
        larger           -> the mask's corner cells, max 4
    for each anchor (x, y):
        drop from layer-1 downward to the first occupied cell, or to layer 0
        emit ScaffoldColumn(x, y, fromLayer, toLayer)
    recompute ground-connectivity   // this orphan now supports what rests on it
```

Ascending order plus the recompute means one scaffold can carry a whole stack — a 12-part tower floating in the air costs one column, not twelve.

**Scaffolds are not parts.** Separate collection, not in `GridMap` occupancy, **not saved**, regenerated from scratch on every Play transition. They must never appear in the save file — otherwise a load/save round-trip bakes them into the player's creation and they accumulate.

They *do* get real colliders: a marble must not fall through a support pillar.

Visual: a distinct scaffolding look — thin translucent pillar, desaturated, subordinate to the player's own bricks so the build still reads as theirs. Deliberately *not* Duplo-styled; it should look like engineering scaffolding, so it's obvious what the player built and what the game added.

**Build-mode preview**: render the same scaffolds ghosted while building, so the player sees what will be added before committing. Recomputed on mutation, cheap because it's incremental.

**Toggles**: `auto-support on/off` (off → orphans simply float, physics-free, for players who want the magic-castle look) and `hide scaffolds` (visual only; colliders stay).

**Edge cases**:
- Column would land on a track part → it stops on top of that part. Slightly odd-looking, structurally fine, accepted.
- Column would pass through the marble's path → unavoidable in general; the build-mode preview is the mitigation, since the player can see it and move the piece.
- Orphan directly above another orphan → handled by the ascending-layer order.

---

## 6. Track connectivity

Each track `PartDefinition` declares ports:

```csharp
struct TrackPort {
    Vector2Int cell;      // local footprint cell
    Facing     facing;    // N | E | S | W  (rotated with the part)
    float      heightMm;  // channel floor height above the part's base
}
```

On placement, `ConnectionSolver` checks each port against the opposing port of the neighbouring cell — matching facing, height within ~2 mm → connected edge.

`TrackGraph` (built incrementally, not rebuilt per placement) drives:

- **Visual feedback** — connected seams glow / unconnected port ends pulse. Biggest usability win in the whole build mode: kids see instantly why the ball will fall off.
- Start→Goal reachability check ("your track reaches the goal!").
- Optional guided-ball assist mode (see §7).

Consider `com.unity.splines` to author centerlines for future long/curved parts; not required for the current 6 parts.

---

## 7. Play mode

**Transition**: generate scaffolds for floating parts (§5.1), freeze the build — mark parts static, disable build input map, enable play HUD. Optionally bake visual meshes into per-chunk combined meshes (colliders untouched) if draw calls are a problem. Returning to Build destroys all scaffolds.

**Release**:
- From a placed **Start** part (gate opens, marble drops in), or
- **Free drop** — click anywhere, marble spawns just above the cursor ray hit.

**Marble**: sphere rigidbody per §2, plus the soft-assist corrective force (§13). Multiple simultaneous marbles, spawn-rate limiter, per-marble colors.

**Controls**: release, release-N, reset (despawn all + rewind to build state), slow-motion (`Time.timeScale`, physics timestep scaled with it), camera modes free / follow-marble.

**Outcomes**: `GoalTrigger` on a Goal part counts arrivals; HUD shows time-to-goal, marbles finished / lost. Out-of-bounds plane despawns escapees and reports "marble lost" with the position, so the builder learns where the track leaks.

**Marble help** setting (off / gentle / strong) drives `assistStrength` — see §13 for the full design and its guardrails.

---

## 8. Persistence

JSON, versioned, part-ids as stable strings so adding/reordering parts never breaks old saves:

```json
{
  "version": 1,
  "name": "Big Loop",
  "bounds": { "min": [-3, -8, 0], "max": [12, 9, 6] },
  "parts": [
    { "id": "track_2x2", "x": 4, "y": 6, "layer": 2, "rot": 1, "color": 3 }
  ]
}
```

No `gridSize` — the world is unbounded (§4.2). `bounds` is derived metadata, stored only so the loader can frame the camera and pick a thumbnail angle without walking every part.

- `SaveMigrations` — `version` field with a chain of upgraders from day one.
- Thumbnail: render-to-texture snapshot stored alongside for the load browser.

### 8.1 `ISaveStore` — required by WebGL

WebGL has no real filesystem; `persistentDataPath` is emscripten IDBFS and flushes asynchronously, so a returned write is not yet durable. Abstract it:

```csharp
interface ISaveStore {
    Awaitable<string[]>  ListAsync();
    Awaitable<string>    LoadAsync(string slot);
    Awaitable            SaveAsync(string slot, string json);  // completes only once durable
    Awaitable            DeleteAsync(string slot);
}
```

- **macOS** → `FileSaveStore`, `persistentDataPath/creations/*.json`.
- **WebGL** → `IndexedDbSaveStore`, a `.jslib` plugin talking to IndexedDB directly. Do **not** use `PlayerPrefs` (per-key size limits) and do not rely on Unity's implicit IDBFS sync.

All call sites are async from day one — retrofitting async onto a synchronous save API is a painful refactor, and the macOS path costs nothing to write this way.

### 8.2 Sharing

Export/import a creation as a `.json` file (gzip + base64 for short share codes). On WebGL this needs a `.jslib` plugin: `Blob` + object URL for download, `<input type="file">` for import. Same `ISaveStore`-adjacent seam, separate `ICreationTransfer` interface.

---

## 9. Project structure

```
Assets/
  Art/Meshes/*.stl                 // source of truth, ScriptedImporter handles them
  Art/Materials/
  Parts/Definitions/*.asset        // PartDefinition SOs
  Parts/Colliders/*.prefab
  Parts/PartCatalog.asset          // palette ordering + categories
  Scripts/
    Core/          GameMode state machine, bootstrap, physics config
    Grid/          GridCoord, GridMap, Footprint, PlacementValidator,
                   GroundConnectivity, ScaffoldGenerator
    Parts/         PartDefinition, PartCatalog, PlacedPart, PartFactory
    Build/         BuildController, GhostPreview, PlacementRaycaster,
                   CommandStack, Commands/
    Track/         TrackPort, TrackGraph, ConnectionSolver
    Play/          PlayController, MarbleSpawner, Marble, MarbleAssist,
                   GoalTrigger, OutOfBounds
    Camera/        OrbitCamera, FollowCamera, FrameBuild
    World/         GroundPlaneRaycaster, InfiniteGridRenderer, Chunk, ChunkMap
    Persistence/   SaveModel, SaveMigrations,
                   ISaveStore, FileSaveStore, IndexedDbSaveStore, ICreationTransfer
    UI/            Palette, Toolbar, BuildHud, PlayHud   (UI Toolkit)
  Plugins/WebGL/   SaveStore.jslib, FileTransfer.jslib
  Shaders/         InfiniteStudGrid.shadergraph
  Editor/
    StlScriptedImporter, PartDefinitionInspector,
    FootprintDeriver, ChiralityAnalyzer, MirrorPartGenerator,
    PartValidatorWindow
```

Packages: URP, Input System, UI Toolkit (built in), optionally Splines.

### 9.1 Build & hosting matrix

Two WebGL hosts with different capabilities, so **two WebGL build configs**, not one compromise:

| | Self-hosted | GitHub Pages |
|---|---|---|
| Can set `Content-Encoding: br` | yes | **no** — serves files as-is |
| Compression | Brotli, fallback **off** | Brotli, fallback **on** |
| Cost | smallest + fastest load | `.loader.js` ships a JS decompressor; slower startup, ~same transfer size |

GitHub Pages cannot set response headers, so without the decompression fallback the browser receives Brotli bytes it was never told to decode and the build fails to load. The fallback is the price of Pages hosting — enable it there, not everywhere.

Ship both from one CI job (`unity-builder` GitHub Action): `webgl-selfhost`, `webgl-pages` (auto-deployed to Pages on green), `macos` (Apple Silicon / Universal, Metal). Pages doubles as the always-current playable link for testing on other machines.

**Common settings**: IL2CPP, Managed Stripping High, unused engine modules stripped, `link.xml` preserving save-model types (§11).

---

## 10. Milestones

| # | Goal | Done when |
|---|---|---|
| M0 | Project + import pipeline | Unity 6.5 URP project, `git init`, STL importer working, all 20 parts render at correct scale with smooth normals, chirality analysis + mirror generation reviewed once. **CI producing WebGL + macOS builds, Pages deploy live, on day one** |
| M1 | Grid & placement | Orbit camera, infinite grid ground, ghost preview, place/delete blocks with snapping, multi-layer parts, support rules. 2000-part stress scene profiled on WebGL — decides GameObject vs. instanced rendering |
| M2 | Editing | Rotation, colors, box select, undo/redo, async save/load via `ISaveStore` on both targets, thumbnails |
| M3 | Track + supports | Ports, `TrackGraph`, connection visual feedback, start & goal parts, auto-scaffolding (§5.1) with build-mode preview |
| M4 | Play | Mode switch, marble physics tuned per §2, soft assist (§13) with centerlines on the track parts, release/reset, out-of-bounds, goal detection, timer. **Physics profiled on WebGL at target marble count** |
| M5 | Feel | UI Toolkit palette, marble-help setting, assist tuning from playtest, sound (rolling loop pitched by speed, clacks on impact — gesture-gated audio init), particles, camera follow, optional decorative baseplate |
| M6 | Content | Challenge/level mode, sandbox scoring, more STL parts, share codes + file import/export |

M0–M2 give a usable brick editor; M4 is the first genuinely fun build. Ship-quality vertical slice = M0–M4.

**Keep the WebGL build in CI from M0.** WebGL problems — heap exhaustion, stripping breaking reflection-based serialization, plugin gaps, load time — surface as *late* failures if the target is only exercised at the end, and each one can force architectural rework. A green WebGL build every milestone is the cheapest insurance in this plan.

---

## 11. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Marble tunnels through thin track walls | 10× world scale, `defaultContactOffset 0.002`, 120–200 Hz timestep, `ContinuousDynamic`, thicken collider walls beyond the visual mesh |
| Marble slides instead of rolls | Raise `maxAngularVelocity` (default 7 is the usual culprit), tune friction |
| Mesh colliders tank performance | Authored primitive-compound colliders per part type (§3.3); render mesh never used for collision |
| New STL doesn't fit the 16 mm grid | Importer warns on non-`n*16-0.2` bounds; `PartValidatorWindow` shows the grid overlay |
| Part-id churn breaks saves | Stable string ids + save `version` + migration chain from v1 |
| Draw calls at large builds | GPU instancing → `RenderMeshInstanced` batched path → chunk mesh combine in Play mode |
| PhysX non-determinism | No replay-dependent features; record transforms if ever needed |
| **WebGL heap exhaustion** on big builds | Welded meshes, non-readable render meshes, primitive colliders, instanced rendering; measure with a 2000-part scene at M1 |
| **WebGL single-thread physics stall** | Marble cap (16 soft / 32 hard), `maximumDeltaTime` clamp, drop 120→90 Hz before ever dropping CCD |
| **IL2CPP stripping breaks JSON serialization** | Explicit `link.xml` for save model types; caught early by keeping WebGL in CI from M0 |
| **Save loss on WebGL** (async IDB flush) | `ISaveStore.SaveAsync` completes only once durably flushed; autosave before mode switch and on `pagehide` |
| Player gets lost in an unbounded world | Origin marker, frame-build (`F`), return-to-origin, far-from-home nudge (§4.2) |
| Infinite grid shader moirés / z-fights at distance | `fwidth` AA + distance fade + fog; ground quad is view-only and never collided against |
| Scaffolds leak into save files and accumulate | Scaffolds live outside `GridMap`, are never serialized, and are rebuilt from scratch on every Play transition (§5.1) |
| Auto-mirror silently duplicates a non-chiral part | Detector classifies Redundant/Chiral/**Ambiguous**; author confirms in `PartValidatorWindow`, verdict stored (§3.4) |
| Footprint mis-derived from an inset mesh (`u_turn`) | Two-source derivation: bounding box + underside sockets; disagreement flags for confirmation (§1.1) |
| Triangle budget as the part set grows (2x10 is 22k tris) | Weld on import; add a Blender LOD pass if the M1 stress test demands it (§14) |
| Soft assist reads as "magnetic" / fake | Project out the tangent component, cap strength well below gravity, apply only inside the channel (§13.0); default low and tune from playtest |
| New part ships without a centerline | Assist is opt-in per part — empty centerline means pure physics, never a break (§13.1) |

---

## 12. Decisions

**Settled**

1. ~~Target platforms~~ → **macOS + WebGL**, mouse/keyboard only, no touch. See §0.
2. ~~Baseplate size~~ → **open universe**, infinite ground plane at layer 0, optional decorative finite baseplate. See §4.2.
3. ~~Support policy~~ → **build freely in mid-air; auto-generate scaffolding on Play transition.** See §5.1.
4. ~~WebGL hosting~~ → **both**: self-hosted config (fallback off) + GitHub Pages config (fallback on), from one CI job. See §9.1.
5. ~~Mirror parts~~ → **auto-generated for chiral parts**, detector proposes / author confirms. See §3.4.

6. ~~Assist mode~~ → **soft assist (option C)** from the start: rigidbody truth + tunable corrective force, slider defaults low. See §13.
7. ~~Blender pass~~ → **skip for now.** ScriptedImporter only; revisit for LODs if the M1 stress test demands it. See §14.

All decisions settled. Nothing blocking M0.

---

## 13. Decided: soft assist (option C)

**Mixed-age audience → rigidbody physics as the truth, plus a tunable corrective force.** Sections 13.1–13.3 record the alternatives and why C won.

### 13.0 What ships

A weak spring-like force pulls the marble toward the local track centerline while it is inside a channel:

```csharp
// per FixedUpdate, only while the marble is within a track part's channel volume
Vector3 toCenter = nearestCenterlinePoint - marble.position;
toCenter = Vector3.ProjectOnPlane(toCenter, pathTangent);   // never push along travel
marble.AddForce(toCenter * assistStrength, ForceMode.Acceleration);
```

Key properties:

- **`assistStrength = 0` is exactly option A.** One motion model, one code path, one slider. Ship at a low default, expose it in settings ("Marble help: off / gentle / strong"), tune from playtesting rather than from theory.
- **Project out the tangent component.** The force must correct lateral drift only — never accelerate or brake the marble along its direction of travel, or it stops feeling like gravity is doing the work and the whole run reads as fake.
- **Only inside the channel.** Once the marble is airborne or off the track it is pure physics again, so leaving the track still *looks* like a real consequence. Assist keeps marginal cases on the rails; it doesn't resurrect a marble that has genuinely left.
- **Never exceed gravity.** Cap `assistStrength` well below `98.1` (§2) so the corrective force can never lift the marble or hold it against a wall. Above that threshold it stops reading as banking and starts reading as magnetism — the failure mode that makes this option feel uncanny.

### 13.1 Centerline data

Assist needs a centerline, but only a **rough** one — this is why C is cheap and B is not. Option B needs an exact, complete, always-valid path graph; C needs an approximation and degrades gracefully when it's poor.

Add to `PartDefinition`:

```csharp
Vector3[] centerline;   // local-space polyline through the channel, per port pair
```

Authored per part type, ~4–8 points for a curve, 2 for a straight. Derivable for simple parts by sampling the channel floor's lowest points along the part axis; hand-fixable in `PartValidatorWindow`. A part with **no** centerline simply gets no assist and falls back to pure physics — so a new STL never breaks, it just doesn't benefit until someone adds one. That graceful-degradation property is the whole reason this is safe to ship early.

### 13.2 Considered: rigidbody only (option A)

The question was what happens when a marble reaches a gap, a too-sharp curve, or a badly-banked turn.

The marble is pure PhysX. It flies off bad tracks, overshoots gaps, rattles down slides. **Reachable at any time by setting `assistStrength = 0`** — it is not so much rejected as subsumed.

- **For**: the failure *is* the game. Watching a marble launch off a curve is the feedback loop that teaches banking and gradient, and it's the thing kids actually laugh at. Zero extra code. Track-connection glow (§6) already tells the player where the leak is before they run it.
- **Against**: a young child who cannot yet reason about why it failed may just get a marble that never finishes. Frustration instead of learning.

### 13.3 Rejected: spline-constrained marble (option B)

The marble is bound to the `TrackGraph` centerline, simulated as 1-D energy along the path (gravity along the tangent, friction term). It cannot leave the track. At a gap, it either stops or hops to the nearest connected port.

- **For**: always finishes, always readable, cheap and stable. Immune to every physics failure mode in §11 — no tunnelling, no marble-cap pressure, no WebGL timestep worry.
- **Against**: it's a different game. No emergent behaviour, no surprises, and it silently rewards bad track design — the player never learns the gap mattered. Also needs `TrackGraph` to be complete and correct, so it's blocked on M3 and gets *less* reliable as more exotic parts arrive.
- **Cost**: real. A second motion model, a second camera-follow path, and every new part type needs a valid centerline authored or it breaks.

### 13.4 Why C won

Audience is **mixed ages** — both the 3-year-old who needs the marble to arrive and the 7-year-old who wants to see it crash. That rules out picking a fixed point on the spectrum, and C is the only option that *is* the spectrum: one slider spans from A to nearly-B, with one motion model behind it.

B would have meant a second motion model, a second camera-follow path, and a hard dependency on `TrackGraph` being complete and correct for every future part — getting *less* reliable as more exotic STLs arrive. C needs only a rough centerline and degrades to pure physics where one is missing (§13.1).

---

## 14. Decided: no Blender pass (for now)

Currently §3 assumes a `.stl` ScriptedImporter does everything in-Unity. A Blender batch step (headless `blender --python`, run in CI or on demand) would sit between the STL and Unity. What it could add, in increasing order of "actually worth it":

1. **Decimated collision meshes** — only matters for the `MeshCollider` *fallback* path. If primitive-compound colliders (§3.3) cover every part, this is dead weight. Current 20 parts all look approximable by primitives.
2. **LODs** — an open universe means zoomed-out views with many parts. `building_block_2x10` at 22 k tris × a large build is real cost, especially on WebGL. Blender's decimate modifier generates LOD1/LOD2 cheaply. **This is the strongest argument for the pass.** Unity has no built-in mesh decimation.
3. **Proper UVs** — only needed if parts ever get textures/decals/stickers. The current flat-color Duplo look doesn't need them, and STL carries none.
4. **Robust normal/weld handling** — Blender does this better than a hand-written importer, but a 30° smoothing weld in the importer is genuinely sufficient for this geometry.
5. **Mirror generation** — could live in Blender instead of Unity (§3.4). No advantage; the transform is trivial either way, and doing it in Unity keeps the `PartDefinition` mask/port mirroring in the same place as the mesh mirroring.

**The tradeoff**: a Blender step adds a toolchain dependency (a pinned Blender version in CI, a Python script to maintain) and breaks the "drop an STL in the folder and it appears" property that makes extending the part set painless. That property is worth a lot given the set went 6 → 20 in one pass and will keep growing.

**Decision: start without it.** Ship the ScriptedImporter alone through M4.

**Revisit trigger**: the M1 WebGL 2000-part stress test. If triangle count (not draw calls) is the bottleneck, add the pass **for LODs only** — the one item on the list Unity genuinely cannot do itself. Draw-call bottlenecks are answered by instancing (§5) instead, not by Blender; distinguish the two before reaching for the toolchain.

If it does get added, keep it a CI step that reads `Art/Meshes/*.stl` and writes `Art/Meshes/Generated/*_lod1.mesh`, so the drop-in property survives.
