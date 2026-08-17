# Block Marble Run — Design & Implementation Plan

Unity 6.5 (6000.5.x), URP, PhysX (built-in 3D physics), Input System, UI Toolkit.

Two modes: **Build** (Duplo-style brick placement) and **Play** (marble released, rigidbody physics).

**Targets: macOS (Apple Silicon, Metal) and WebGL.** World is an open universe — no bounded baseplate.

**State: M0–M5 built.** Building, editing, saving, track connectivity, auto-scaffolding, play mode with
marbles, water, sound and camera work are all in. M6 (challenge mode, share codes) is not started. This
document is kept as the reasoning behind the code, not as a plan ahead of it: where a section describes
something that was later measured or rebuilt, the section says so rather than being quietly rewritten.

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

Every Z value decomposes as `bricks × 19.2 (+ 4.6 if top studs)`: 19.2, 23.8, 38.4, 43.0. The model held across all 20 parts — no new height machinery needed, just `heightLayers > 1`. (The "layers" column counts *bricks*; the grid has since been halved, so each of these is two grid layers — §1.5.)

Derived constants (real Duplo geometry — assets match it exactly):

```
STUD_PITCH_MM   = 16.0      // XY grid
BRICK_HEIGHT_MM = 19.2      // one brick tall = 1.2 * pitch
LAYER_HEIGHT_MM = 9.6       // the grid's vertical step: half a brick (§1.5)
STUD_HEIGHT_MM  = 4.6       // 23.8 - 19.2
CLEARANCE_MM    = 0.2       // n*16 - 0.2 per axis
```

Track parts are exactly 1 brick tall with no top studs → **uniform grid, no special cases**.

### 1.1 Bounding boxes are not footprints

`u_turn` is **78.2 mm** across — but `5 × 16 − 0.2 = 79.8`. It is 1.6 mm (one wall thickness) short of its own footprint, because the outer arc wall doesn't reach the grid edge. A naive `round(bounds / 16)` gives 4.89 → 5 by luck, but the tolerance check would fire a false alarm, and a future part inset by 8 mm would round to the *wrong* footprint outright.

So footprint derivation is two-source (§3.2): bounding box gives a candidate, **underside socket geometry** confirms it. Where they disagree, the importer flags the part for human confirmation rather than guessing.

### 1.2 Meshes are not all centred on their footprint

Most are, but two are not: `u_turn` spans −15.9…+62.3 mm in X, and `u_turn_slide` is offset by a full stud in Y. Positioning those as though centred draws the geometry a stud away from the cells it occupies, so their channels appear to join one stud *inside* the neighbouring piece while the grid considers them correctly aligned.

Each part therefore stores a pivot offset, applied through the placement rotation. It is measured from the mesh's **minimum corner**, not its bounding-box centre: a part that stops short of its footprint does so on one side only — `u_turn`'s open mouth side has no wall to reach the boundary — so centring splits that 1.6 mm shortfall across both sides and leaves the piece a fraction of a stud out of true. The walled side is flush with the grid and is what the geometry should align to.

### 1.3 Parts are not all solid prisms

Undersides measured per cell: `slide_2x4` reaches its base everywhere, but `slide_curve_4x4` ramps from 18 mm down to 0, so its raised end occupies **only the upper layer**.

Occupancy is therefore stored **per layer**, not as one footprint repeated up the part's height. Treating a ramp as solid claims the space under its raised end — space a support pillar needs to stand in — and the pillar then collides with the very part it was meant to carry.

### 1.4 Pivot / parity gotcha

`1x2` spans 2 studs in X (center falls on a stud *boundary*) and 1 stud in Y (center falls on a stud *center*). Mixing parities breaks naive integer snapping.

Fix: work internally in **half-stud units (8 mm)**. A part occupying cells `[minCell, minCell+size)` sits at
`worldXY = (minCell + size * 0.5) * STUD_PITCH`. All parities collapse to one formula.

### 1.5 The grid steps a half-brick, not a brick

Plates — half-height bricks — are a real Duplo part and the obvious way to fine-tune a slope by a
small amount. A grid whose only vertical step is 19.2 mm cannot express one at all.

So the layer is **9.6 mm** and every full-height part is two layers. This is a change of unit, not of
model: nothing about clutch, support or channel heights depends on the step being a whole brick, and
the channel-floor rule (§6.1) was always stated against the *brick* pitch, which is unchanged. What it
does change is every stored layer index, which is why save v3 doubles them (§8).

The funnels added later settled the question independently: they are **28.8 mm** tall — one and a
half bricks — and would not have fitted a brick-stepped grid at any height at all.

Worth noting what does **not** follow: a Lego plate is a third of a Lego brick, and the Lego→Duplo
scale is ×2 on both pitch and height. A Duplo half-brick is therefore not "a Duplo plate" in the Lego
sense, and no finer subdivision buys anything until a part actually needs one.

### 1.6 The set as it stands

The table above is the twenty parts the pipeline was designed against. It has grown, and every
addition arrived through the same pipeline rather than through a special case:

| Source | Count | How it is made |
|---|---|---|
| STLs in `Art/Meshes` | 26 | dropped in the folder, analysed on import |
| Generated mirrors | 5 | chirality analysis, §3.4 — the two handed slides and all three funnels |
| Generated plates | 6 | half-height copies of the six blocks, §3.6 |

**37 `PartDefinition` assets** in the catalog, plus support pillars of any height made at runtime from
one of the modelled ones (§3.7). One of the 26 STLs was not modelled for this: `stalk_2x2` is a Lego
part converted from OBJ at Duplo scale (§3.8). The funnels are the largest parts in the set at 10×10 studs
and the most interesting for derivation — the useful antistuds are a pair on the lip, far from the
footprint's corners, with most of the interior open (§3.5).

---

## 2. World scale & physics tuning

The balls these channels are built for are **24.5 mm**, running in a 32 mm trough. (An earlier draft assumed 13 mm; the real size is nearly twice that, which makes the situation less extreme than first reckoned but does not change the conclusion.) PhysX's `defaultContactOffset` is 10 mm by default — still most of a ball radius, and the trough walls are thinner than that. Two fixes; **use both**:

**Scale the world 10×** (1 Unity unit = 10 cm real):

```
STL import scale   = 0.01          // brick = 0.318 units, 24.5 mm ball = 0.245 units
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

Ball rigidbody:

```
collisionDetectionMode = ContinuousDynamic
maxAngularVelocity     = 200      // CRITICAL: default 7 rad/s hard-clamps a rolling
                                  // sphere and it "sticks"/slides instead of rolling
interpolation          = Interpolate
angularDamping ≈ 0.02
```

### 2.1 Balls are a data type, not a constant

Size and material are authored per ball, so a run can be tried with a heavy steel ball or a light plastic one:

| | diameter | density | mass |
|---|---|---|---|
| Plastic | 24.5 mm | 1.05 g/cm³ | ~8 g |
| Glass | 24.5 mm | 2.50 g/cm³ | ~19 g |
| Steel | 24.5 mm | 7.80 g/cm³ | ~60 g |
| Small glass | 16.0 mm | 2.50 g/cm³ | ~5 g |

**Mass is derived from density**, never entered directly. Gravity accelerates every ball alike regardless of mass, so the number does nothing on its own — it tells only where momentum meets something else, and hand-entered masses invite values no real material would have. Friction and bounce carry most of the felt difference between materials.

The smaller ball earns its place by rattling in a channel built for 24.5 mm, which shows how much of the run's behaviour the channel width is responsible for.

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
- **Top studs / bottom sockets**: read off an exact height map, not clustered — see §3.5, which is where most of the pipeline's real difficulty turned out to live.
- **Height in layers**: `round(bodyHeight / 9.6)` where `bodyHeight` = total minus 4.6 if studs were detected. The funnels are the reason the divisor is a half-brick (§1.5).
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

**Compare volume, not vertices.** The first implementation compared quantised *vertex sets* and was wrong on four parts out of twenty, plus indecisive on two more. Mirroring a triangulated mesh does not reproduce the original vertex positions even when the solid is identical, so that test was measuring **tessellation**, not shape.

The working test samples points across the surface — each triangle getting a share proportional to its area, so dense regions are not over-weighted — voxelises at 2 mm, and compares occupancy. Sampling uses a fixed-seed xorshift rather than `System.Random`: a verdict that changed between runs or Unity versions would be worse than a wrong one.

Results over the current 20 parts (mirror × each of 4 rotations, best score):

| Part | vertex test | volume test | verdict |
|---|---|---|---|
| `track_2x2`, `crossing_2x2`, blocks | 1.00 | 1.00 | redundant |
| `curve_2x2` | 0.90 *(ambiguous)* | **1.00** | redundant |
| `bridge_2x3` | 0.92 *(ambiguous)* | **1.00** | redundant |
| `slide_2x2` | 0.23 *(wrong)* | **1.00** | redundant |
| `slide_2x4` | 0.50 *(wrong)* | **0.99** | redundant |
| `curve_4x4` | 0.12 *(wrong)* | **0.99** | redundant |
| `terminal_2x2` | 0.22 *(wrong)* | **0.99** | redundant |
| `u_turn` | 1.00 | 0.98 | redundant |
| `slide_curve_4x4` | 0.01 | **0.26** | chiral → generate |
| `u_turn_slide` | 0.03 | **0.27** | chiral → generate |

Only two parts in the set are genuinely handed. The scores are now sharply bimodal — 18 at 0.98–1.00, two at 0.26 — with nothing in between, so `Redundant ≥ 0.90` / `Chiral ≤ 0.75` is a comfortable split rather than a knife edge.

Sample density matters: `slide_2x4` scored 0.85 at 60k samples and 0.99 at 250k, while the genuinely chiral parts stayed near 0.26 at every density. **Under-sampling looks exactly like chirality**, so the count is set well past where the scores stop moving.

The classification is still `Redundant` / `Chiral` / `Ambiguous` with the verdict stored on the `PartDefinition`, and `PartValidatorWindow` shows both meshes for anything ambiguous. Fully automatic mirroring would silently ship duplicate parts — a palette offering the same piece twice is a bug nobody reports, they just find the palette confusing.

Because verdicts are stored, changing the analysis needs an explicit **Reanalyse Mirrors** pass: it re-derives every verdict and deletes mirror assets that are no longer justified. Without it, the four bad mirrors would have stayed in the palette permanently.

### 3.5 Studs and sockets are read, not guessed

`topStuds` and `bottomSockets` are the two masks everything else rests on: they decide what clutches
to what, where scaffolding may stand, and where the alignment guides draw. They are also the two that
were wrong the longest, in ways that only showed up as strange behaviour three systems downstream —
a curve that would not accept a brick on its raised end, a pillar refusing to appear, an antistud
highlighted over open air.

Two rules earn their keep:

**A stud is a plateau with a body under it, not the highest thing in the column.** The first rule was
"highest surface in this cell", which is true of a brick and false of anything with a rim: every large
funnel grew a ring of studs round its edge, because the edge *is* the highest thing there. The test is
now two plateaus in the same cell — a flat top, and another flat surface exactly 4.6 mm below it,
which is what a moulded stud actually is.

**A socket needs the underside to be flat there, not merely low there.** Bounding-box fill was the
original shortcut and it is wrong in both directions: it claimed a slide curve's corner of open air,
and it swallowed the two lip studs that are the only thing a funnel hangs from. Rasterising each
triangle exactly fixed the coverage; the remaining errors were tolerance. Measured on `u_turn_slide`,
a genuine antistud samples **64 points at exactly 0.0** — one moulded plane — while the two false
cells scattered across 19.3–21.1 mm, a ramp merely crossing the cell. The flatness tolerance is
therefore **0.15 mm**, tight on purpose; at 0.5 mm the scatter counted and the u-turn grew two
antistuds in the middle of its slope.

A cell is a socket when at least **15 %** of its area is flat underside at the part's base. Not a
majority: a funnel's lip cell is mostly opening.

**A cell over the hole is not an antistud.** A funnel's underside is flat and at the base plane all
round its throat, so by area alone the cells over the hole looked exactly like cells with an antistud,
and the mask claimed the funnel could clutch down onto studs it would fall straight past. What decides
it is not how much material the cell has but whether there is any where the stud goes: the shaft is
tested against the central 9.5 mm of the cell, and a third of that gone is enough to refuse it.

**The masks are checked by eye, once, in text.** `PartMaskReport` writes `PartMasks.txt` — every part
as an ASCII grid, `T` stud, `o` antistud, `B` both, `.` neither, `-` outside the footprint. Reading
thirty-seven small diagrams found three real errors that no amount of looking at the 3-D view had, and
it is regenerated whenever the derivation changes.

### 3.5.1 Finding the hole a ball drops through

A funnel is placed by where its hole lands and by nothing else, so the hole has to be data. It is
found from above: samples that no triangle covers at any height, flood-filled from the border so that
what remains is enclosed by geometry on every side.

Occupancy could not answer it. The masks are per cell, and at the throat the sloping wall passes
through the same cells as the hole, so every one of them reads as solid.

**What separates a hole from a gap is the ball, not the shape.** Both u-turns enclose a slot between
their arms that is genuinely open from top to bottom - the detector is right about that - and marking
it would draw a target on the one place a ball cannot go, because the slot is **18 mm** across against
a **24.5 mm** ball. The funnels' throats are **28 mm** and round. So the test is the narrow side of
the shaft against the ball's diameter, with an aspect check so a long slot cannot creep in on width
alone. Measuring "is there material above it" separates neither: there is none in either case.

### 3.5.2 A channel measured from a shelf, not from the base

Every part in the set carries its channel floor 6.4 mm above its own base - except the funnels, which
carry theirs **7.2 mm above the stud shelf an incoming piece plugs onto**. A track piece standing on
that shelf arrives 0.75 mm below the chute, and a ball at the slowest point of its run stops against
the step.

Measuring it took two wrong answers first, both worth recording. Comparing the funnel's chute to its
own base gave 3.2 mm and made it look like a different channel convention; the funnel's reference
surface is the shelf, so that comparison was between two different planes. Then the automatic
derivation walked inward from the shelf *towards the middle of the part*, which on a corner shelf is
diagonal, crossed the bowl's slope, and returned a different answer for each of three identical
junctions - the disagreement between the funnels was the tell, since the geometry is the same.

**The fix is in the collider, not in the grid.** `colliderOffsetUnits` drops a part's collision
geometry below its drawn mesh, and the funnels drop by the measured 0.75 mm, which makes the two
channels continuous. The whole collider moves, chute and cone and throat together: dropping the chute
alone would move the step to where the chute meets the cone, which is inside the piece, where a ball
is committed and slow. What moves is the funnel against the world, by three quarters of a millimetre,
and only in collision - the mesh is drawn exactly where it was.

Welding (§7.1) has to know about it, or the step comes back in play mode alone: the welded run is
built from each part's collider transform rather than from the part's own.

### 3.5.3 A funnel has no mouth on its perimeter

The obvious repair for the funnels deriving no channel mouths was the floor rule: a channel was
accepted only at `6.4 + k*19.2`, a *brick*-pitch rule written when a layer was a whole brick, and the
funnel's chute is at 16.8 mm - one grid layer plus a channel. The rule is now per layer,
`6.4 + k*9.6`, which is what the half-brick grid (§1.5) implies and which changes no other part's
mouths at all: every one of the 26 derives exactly what it did before.

It does not give the funnels mouths either, and the height map says why. **Their chute never reaches
the footprint edge.** It begins a stud inside, where the stud shelf ends, so the piece feeding a
funnel stands *on top of* it rather than beside it. The port model describes mouths at a part's
boundary meeting mouths at another's; this junction is not that shape and cannot be made into it by
widening a tolerance.

**A port does not have to be on the perimeter.** Nothing downstream requires it: a port says where a
channel ends and which way it faces, and the matching compares midlines and heights. So the funnel
gets a mouth at the boundary between its stud shelf and its chute - inside its own footprint - facing
back out over the shelf. A piece standing on the shelf then meets it exactly as two track pieces meet
each other, and channel snapping, open-mouth marking and welding all follow from the one declaration
rather than from three special cases.

Two details decide whether it works:

- **The mouth sits on the chute side of the shelf, not on the footprint edge.** On the edge, a feed
  snaps up *against* the funnel instead of onto it, leaving the shelf as a gap for the ball to drop
  into. This is the opposite of the perimeter convention, for the opposite reason: a perimeter mouth
  faces out of the part, this one faces out of the chute across a shelf that is still the funnel's.
- **It is declared at the height a feed arrives at** (shelf + 6.4), not at the chute's own 7.2. The
  0.8 mm between them is the lip, removed by lifting the run (§3.5.2); declaring the true height
  instead would put the join half a layer out and the solver would refuse it.

One consequence worth stating, because it cost a defect: the funnel is now *in* the network it is the
reference for, and the network takes the largest demand in it - so without care the funnel rises with
its feed and the step is exactly where it started. A part that asks for a lift never takes one.

### 3.6 Plates are generated, not modelled

A plate is a brick with the middle 9.6 mm removed. Cutting one out of the block mesh gives six parts
for no modelling, and — more to the point — guarantees they stay consistent with the blocks they are
cut from: a plate's studs are the block's studs by construction, not by a second author's care.

### 3.7 Pillars are procedural

A ten-brick column used to be ten placed bricks: ten map entries, ten colliders, ten seams for a
marble that strays onto it, ten lines in the save file. One pillar of the right height is one of each.

`PillarMeshBuilder` finds the plain shaft of the modelled pillar — **measured** as the longest run
whose cross-section does not change, rather than written down — and lengthens or shortens it, leaving
the moulded base and studded top untouched. Definitions are made at runtime and named for their
height (`pillar_2x2x9`), so a save refers to a part that the catalog will not contain until the loader
asks for it; `ProceduralPillars.Resolve` is the loader's fallback for exactly that.

The scaffolder prefers a pillar, falls back to bricks, and finishes with a plate when the remaining
gap is half a brick. Raising an existing support **re-cuts the pillar** rather than stacking bricks on
top of it, which is why a lifted build keeps looking like one column instead of growing a totem pole.

### 3.8 Foreign parts and soft parts

`ObjToStl` converts an OBJ into the same STL form the rest of the pipeline reads, with a scale factor
recorded in the file's header comment. It exists so a Lego-scale model can join the set without a
second import path — Lego→Duplo is ×2 on both pitch and height, and the one converted part
(`stalk_2x2`) is deliberately left at Lego scale, because a Lego piece stands on a Duplo stud as-is.

Two consequences worth stating, both learned by getting them wrong:

- **The axis swap mirrors handedness.** Z-up → Y-up by swapping y and z is a reflection, not a
  rotation, so two corners of every triangle must be swapped as well or the mesh imports inside-out.
  It renders almost convincingly until you look at a silhouette.
- **Centre on the part, not on the bounding box.** The stalk's stud is what a player aligns it by, and
  its three stalks lean off to one side; centring the bounding box puts the stud visibly off the grid.
  The pivot comes from the contact patch.

`SoftPart` bends such a part out of a passing marble's way and nudges the marble for it. Unity has no
soft-body solver and a jointed chain would cost a rigidbody per segment; this deforms the mesh
directly — squared with height, so the base stays put — and only the base carries a collider, so a
stalk that has visibly moved aside never stops the ball. The mesh is deformed rather than shaded
because the same mesh draws the palette icon and the placement ghost.

Nothing Duplo-sized clutches to it: it occupies a single grid layer and offers one antistud, so four
of them fit on one 2×2 brick.

---

## 4. Core data model

```csharp
struct GridCoord { int x, y, layer; }              // studs, studs, half-brick layers (§1.5)
// StudUnits = 0.16   LayerUnits = 0.096   BrickUnits = 0.192   (1 unit = 10 cm)

class PartDefinition : ScriptableObject {
    string       id;               // stable string — save files reference this, never an index
    string       displayName;
    PartCategory category;         // Block | TrackStraight | TrackCurve | Ramp | Start | Goal | Special
    Mesh         mesh;
    GameObject   colliderPrefab;
    Vector2Int   footprintSize;    // in studs
    bool[]       footprintMask;    // supports L-shapes / non-rect parts
    int          heightLayers;
    bool[]       layerMasks;       // occupancy per layer, not the footprint repeated (§1.3)
    bool[]       topStuds;         // which cells expose a stud        (§3.5)
    bool[]       bottomSockets;    // which cells have a flat underside (§3.5)
    bool         hasTunnel;        // a way through it — the collider must not be a solid box
    Vector2      pivotOffsetUnits; // mesh vs. footprint, from the min corner (§1.2)
    float        verticalOffsetUnits;   // lifts a part that sits on top of a stud
    bool         soft;             // bends out of a marble's way (§3.8)
    int          softBodyLayers;   // layers it actually claims, which for a soft part is its base
    int          defaultColorIndex;// -1 = take the current palette colour
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

1. Every cell the part actually fills must be free — per layer (§1.3), not the footprint repeated up its height.
2. **Support**: `layer == 0`, **or** a bottom-socket cell rests on a stud below, **or** a channel mouth joins a neighbouring mouth (§6). The two clutch systems are equal in standing.
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

1. Raycast cursor → nearest of {top face of an existing part, ground plane at y=0} (§4.2). This gives the **column** only.
2. Centre the footprint on that column, then compute the resting layer as the **maximum** `ColumnRestLayer` across every base cell of the footprint.

Step 2 is the whole of the clutch behaviour, and getting it wrong is not subtle. Taking the height from the ray's hit point instead makes a whole class of build impossible: laying a long piece across two separated towers means pointing at the **gap** between them, where the ray finds nothing but ground, so the piece drops to the floor. A brick rests on the highest thing beneath *any* part of it, exactly as it would in the hand.

Whether the studs actually line up is a **separate** question, answered by the support rule in §4.1 — the part comes to rest first, then the anti-studs are checked against the studs underneath. Conflating the two is what made placement feel like it was demanding the cursor be over one specific stud.
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

### 5.2 Rendering budget — measured at M1

One GameObject per part, with **one shared material per palette colour**. 2000 parts on WebGL, all visible:

| Variant | Triangles | Frame time |
|---|---|---|
| `MaterialPropertyBlock` per brick, dense mesh | 16.7 M | 20 ms |
| `MaterialPropertyBlock` per brick, **sparse mesh** | 4.2 M | 20 ms |
| Single shared material, no per-brick colour | 16.7 M | 13 ms |
| **Palette materials (shipped) — colour retained** | 16.7 M | **11 ms** |

Two conclusions, both against expectation:

**Geometry is not the bottleneck.** Cutting triangles fourfold changed nothing. The parts are print-resolution — a 2×2 brick is 8328 triangles with every stud and tube modelled — and at 16.7 M triangles the GPU was still running at ~790 M tris/s. It is not struggling; the frame is CPU-bound before geometry ever matters.

**The property block was the cost.** A `MaterialPropertyBlock` opts its renderer out of the SRP Batcher, so per-brick colour was silently costing every brick its batching. Replacing it with one shared material per palette entry keeps the colour and gets the batching back.

The shipped path beats even the colourless control: six shared materials batch at least as well as one, and each carries its colour in the material rather than in a per-renderer override. **2000 parts at 11 ms (~90 fps)** with full per-brick colour, so the 60 fps budget holds with room to spare.

So per-instance colour is not free, but it is cheap *if it comes from a small set of shared materials rather than per-renderer overrides*. `RenderMeshInstanced` is **not** needed yet — revisit only if part counts climb well past 2000, and note it would have to solve colour the same way.

Corollary for later milestones: **anything that colours many renderers must go through the palette materials.** Marbles, scaffolding (§5.1) and connection highlights (§6) are all tempting property-block candidates, and each would reintroduce this cost quietly.

**This is also why the Blender LOD pass stays unbuilt** — §14 made triangle count the trigger, and the measurement says triangle count is not the problem.

---

### 5.1 Auto-generated supports

Floating parts are not an error state — they are a *normal build style*. Requiring a player to hand-build a pillar under every raised curve is exactly the tedium that kills a toy. So: **build freely in mid-air; the game builds the pillars.**

**Real bricks, at placement time.** The original plan deferred scaffolding to the switch into play mode. That reads badly while building — a piece hangs in mid-air with nothing under it and the player has to take on trust that something will appear later. Building the support immediately makes the structure truthful at every moment, and the bricks can be edited or deleted like anything else. Part and pillars are one history entry, so a single undo removes both.

**Only channel parts prop themselves up.** A brick is the player's own structure and may cantilever as far as they like; a run of track is meant to look carried.

**Pillars stand under the mouths**, not under the footprint's corners. Most of a 4×4 curve's square is empty arc that needs nothing; the ends are what a channel actually rests on.

**Each pillar rises to its own mouth's height.** A descending part's two mouths are a layer apart by design, so sizing every pillar to the part's base leaves the raised end permanently one brick short.

**Support is a per-column question, not a whole-part one.** A slide joined to an elevated run is "supported" by that joint, yet its far end still hangs over nothing four studs away. Asking once for the whole part reports it as fine and builds nothing at all.

**Pillars must not claim cells the part needs.** Scaffolding is built before the part enters the map, so the map cannot yet see where the part will be. A pillar that takes one of its cells makes the part fail to place and the whole action is abandoned — silently, after the ghost has already shown green.

### 5.3 Precise placement (hold Shift)

The ordinary loop rests a piece on the highest thing under it, which is what a hand does and is right
almost always. It is wrong in exactly one situation: putting a piece on top of a single pillar. A
pillar is fourteen layers of smooth side and two studs, so the studs are a target a few pixels across
at any sensible zoom, and every near miss dropped the piece on the floor beside it.

Holding Shift changes three things, and each of them is a rule about what the player has already
decided:

- **The ground is never a candidate.** Someone holding Shift over a build is not asking for the floor.
- **The piece slides on its own level**, on a plane at its own height, rather than re-reading whatever
  the cursor happens to strike. A ray that slips past the edge of a pillar carries on to the ground
  behind it, and the piece leaps across the world; a plane has no edge to fall off.
- **The wheel picks between the levels available in that column**, so two pillars of different heights
  side by side both offer their tops rather than the taller one winning.

Two things this cost, both found by reading the HUD rather than by reasoning: `CandidateAt` was being
called twice a frame and the second call consumed the wheel's step, and browsers turn Shift+wheel into
*horizontal* scroll, so the vertical axis is dead there and both axes have to be read.

### 5.4 Alignment guides

A ghost in mid-air says where a piece is but not what it is over, and a stud is a few pixels at any
useful zoom. Thin translucent lines run from the piece's corners down to whatever they line up with —
the part below, or the ground.

They are drawn at the **outward corner of a stud**, not the middle of a cell, and lifted clear of the
surface: a line down the centre of a stud is hidden by the stud. Anchors are chosen in priority order —
channel mouths first, then studs, then sockets, then the outline — because a mouth is the connection
the player is aiming at, and because a piece with no studs at all still needs the vertical reference
that says where it is in space.

**A funnel gets a line down its hole and a circle where it lands.** The corner lines say where the
piece is; this says where the ball will go, which for a funnel is the whole question. It is drawn from
the shaft (§3.5.1) rather than from the outline, because the two are not the same place - the throat
of a 10x10 funnel is a long way inside its footprint - and the circle is the hole's own width, so
lining it up with a channel mouth below is a matter of covering one with the other. A ring of short
bars rather than a disc: a disc hides the thing being aimed at.

Guides run for the ghost, for a grab-mode selection, and for a pasted group, which is the case that
needs them most: a pasted group can be raised and lowered, and it is the only one where every mouth
gets its own set of lines rather than only the corners.

### 5.5 Editing a selection

`V` enters grab mode: click picks, drag box-selects, `A` selects all, Shift+click adds. A selection
can be copied, turned, mirrored, painted and deleted as one.

**Paste is two-stage.** The first click puts the group under the cursor in *placing* state — bright
green, still movable, still turnable, raise and lower with the wheel or `+`/`-`; the second click
commits it. A one-shot paste is unusable for anything but the simplest group, because the position it
lands at is almost never the one that was wanted and the only recovery is undo.

**Right click picks a piece back up**: it is removed and becomes the held piece, with its colour and
rotation, ready to put down again. Shift+right-click is the same thing in precise mode, which is how a
piece gets moved half a stud without being rebuilt. Parts that are not on the palette can be held this
way too.

**Copied supports are re-cut, not re-stacked.** A group containing a pillar, pasted higher up, gets a
pillar of the new length rather than the old pillar with a tower of bricks under it.

### 5.6 Autosave

Two ways to lose a build have nothing to do with the game: pressing `G`, and the browser's back
button. `G` is undoable. The browser is not, so the build is written to a single autosave slot after
every edit and `O` restores it.

Two guards, both from getting it wrong:

- **The opening state counts as already saved.** Otherwise the empty scene is written the moment the
  app starts and the previous session is gone before the player has touched anything.
- **Never write an empty build over a non-empty one.**

---

## 6. Track connectivity

Channel mouths are a **second clutch system, equal in standing to studs**. Two parts whose channels meet — face to face, at the same height — hold each other up exactly as a stud holds an anti-stud. Without this, an elevated run counts as floating along its whole length no matter how solidly it is joined at both ends.

### 6.1 Ports are derived, not authored

The geometry states them unambiguously, so hand-entering ~30 coordinates would only create something to drift from the mesh.

A height map of the top surface is read along each boundary; a wall reads at the part's own height, a channel mouth far lower. The discriminator is that **a channel floor always sits 6.4 mm above a layer boundary** — 6.4, 25.6, 44.8. That single rule separates mouths from walls across all 20 parts, where "lower than the top" does not: `u_turn_slide` has walls at 19.2 and 20.0 mm against a 43 mm top.

Derivation reproduces every part correctly, including that both U-turns have all their mouths on **one side** — `u_turn` on west, `u_turn_slide` on south at 6.4 and 25.6 (a clean one-layer drop).

### 6.2 Mouths are located by their centre line

A channel is two studs wide, so its centre falls on the boundary *between* studs and cannot be named in whole studs at all. Ports are therefore stored in **half-studs (8 mm)**, which makes alignment an integer equality test rather than a tolerance.

Recording a port per boundary *cell* instead — the first attempt — let two runs join while offset by one stud, because a single cell of one mouth still overlapped a single cell of the other. The joint reported as connected while the channels visibly stepped sideways.

Contiguous cells at the same height group into one mouth. `u_turn` is the case that proves the grouping: its two west mouths must stay separate rather than merging across the wall between them.

Half-stud positions need their own rotation. Cell rotation is `(x, y) → (y, w−1−x)`; the `−1` is there because it names a *cell*. Applying that to a centre line would shift every mouth half a stud off the boundary it sits on, so positions rotate as `(x, y) → (y, w−x)`.

### 6.3 Placement has two axes

Studs pull a part **down** onto what is beneath it; channels pull it **sideways to a specific height**. Resting height alone cannot express the second — continuing a run three layers up means placing over empty ground, which no downward rule would ever propose.

`PlacementSolver` gathers candidate layers from both and scores them: a channel join outranks everything, then validity, then lowest wins so pieces settle rather than hover. Alignment must be a **whole** number of layers; a slide mouth meeting flat track half a layer off is a real mismatch, and rounding it would snap parts into a join that does not exist.

### 6.4 Feedback shows the leaks, not the joins

The plan called for glowing every connected seam. Shipped the inverse: a marker on every mouth that leads **nowhere**.

Glowing the joins lights up the entire run and draws the eye to what is already working. The open ends are the actionable information — they are exactly where a marble will leave the track — and absence of a marker then means "connected", which needs no legend. The HUD carries the count.

### 6.5 Start and goal are designations, not parts

There is no STL for either, and inventing one would add art plus a placement path that bypasses the grid clutch rules. Instead any **dead end** — a part with exactly one mouth, which today means `terminal_2x2` — can be cycled through plain → start → goal by pointing at it.

A single mouth is what makes the designation unambiguous: a marble released into a through-piece has two ways to go, and "arrived" at one means only that it passed over. The role is tinted (green start, gold goal), undoable like any other edit, and saved with the creation.

Start-to-goal reachability over the join graph is still unbuilt; it needs play mode to be worth anything.

## 7. Play mode

**Transition**: generate scaffolds for floating parts (§5.1), freeze the build — mark parts static, disable build input map, enable play HUD. Optionally bake visual meshes into per-chunk combined meshes (colliders untouched) if draw calls are a problem. Returning to Build destroys all scaffolds.

**Release**:
- From a placed **Start** part (gate opens, marble drops in), or
- **Free drop** — click anywhere, marble spawns just above the cursor ray hit.

**Marble**: sphere rigidbody per §2, plus the soft-assist corrective force (§13). Multiple simultaneous marbles, spawn-rate limiter, per-marble colors.

**Controls**: release, release-N, reset (despawn all + rewind to build state), slow-motion (`Time.timeScale`, physics timestep scaled with it), camera modes free / follow-marble.

**Outcomes**: `GoalTrigger` on a Goal part counts arrivals; HUD shows time-to-goal, marbles finished / lost. Out-of-bounds plane despawns escapees and reports "marble lost" with the position, so the builder learns where the track leaks.

**Marble help** setting (off / gentle / strong) drives `assistStrength` — see §13 for the full design and its guardrails.

### 7.1 Seams, not gaps, are what catch a ball

Two joined track pieces are two colliders, and a ball crossing the boundary can catch on the edge
itself: PhysX takes the contact normal from the edge rather than from either surface, so it points
somewhere neither surface faces and takes the ball's forward motion with it. The same effect catches
characters on the seams of a tiled floor.

This is why *bridging* the joints made it worse. A bridge does not remove a seam, it adds two. The
only fix that removes a seam is not having one, so `ChannelWelder` merges every joined run of channel
into a single collider on the way into play mode, and the run is one surface from end to end.

### 7.2 Water is Archimedes, not a tuning knob

The floor can be the grid, sand, or water over a sandy bed, and the water level is adjustable in
bricks. Buoyancy is the weight of the water the ball actually displaces, integrated over the submerged
cap — so whether a ball floats falls out of its density against the water's, and is not decided
anywhere in code. A 24.5 mm plastic ball at 1.05 g/cm³ sinks slowly in fresh water, which is what it
does in a bucket. The steel ball goes straight down.

The water surface sits **above** y=0 with the sand bed at the build plane, rather than the water being
at the plane. That leaves the coordinate system untouched — nothing ever goes below layer 0 — while a
brick on the ground is genuinely half submerged and a pillar carrying a run out over the water visibly
runs down into it.

The floor choice and water level are saved with the creation (§8): a run built to end in water is not
the same creation without it.

### 7.3 Sound and splash

**Rolling is speed plus contact, not contact alone.** A ball in free fall is silent at any speed, and
a ball resting against a wall is silent though it is touching something. Contacts open the gate; speed
drives the volume and pitch of the loop. Impacts knock separately.

Sources are positioned in the world with rolloff distances set for the 10× scale — Unity's defaults of
1 and 500 units mean full volume until the camera is fifty metres away, which is to say always. Full
volume to about half a metre, inaudible by twelve.

Splash droplets come from one shared particle system emitting bursts, not a system per splash:
allocating a system per event costs exactly when several balls are landing at once. The crown is
thrown up *and* along the ball's direction of travel, with speeds sized for the scaled gravity — sized
for 9.81 they all fell straight back in.

### 7.4 Watching the ball

`CameraDirector` chooses what to watch; `OrbitCamera` moves. The split is deliberate: choosing a ball
needs to know about play mode and the balls in it, and a rig that moves a transform towards a subject
should have no opinion about where subjects come from.

Four views — **orbit**, **follow** (orbit whose pivot rides along), **chase** (behind and above,
swinging round to the heading), **ride** (close, level, looking down the track ahead). `C` cycles, `N`
takes the next ball, right-clicking a ball watches it. Heading is only read while there is enough
horizontal speed to mean one: a ball dropping straight down has no heading, and reading one out of the
noise spins the camera exactly when the player is trying to watch something.

Build mode stays plain orbit. Nothing being built needs to be chased.

---

## 8. Persistence

JSON, versioned, part-ids as stable strings so adding/reordering parts never breaks old saves:

```json
{
  "version": 3,
  "name": "Big Loop",
  "savedAtUnixSeconds": 1771000000,
  "bounds": { "min": [-3, -8, 0], "max": [12, 9, 6] },
  "floorStyle": 2,
  "waterLevel": 0.12,
  "parts": [
    { "id": "track_2x2", "x": 4, "y": 6, "layer": 4, "rot": 1, "color": 3, "role": 0 }
  ]
}
```

No `gridSize` — the world is unbounded (§4.2). `bounds` is derived metadata, stored only so the loader can frame the camera and pick a thumbnail angle without walking every part.

- Thumbnail: render-to-texture snapshot stored alongside for the load browser.
- Saves are stamped with the time and listed as thumbnails, with delete — one fixed slot turned out to
  mean every creation overwrote the last one. One further slot is reserved for the autosave (§5.6).

### 8.0 The migration chain earned itself

`SaveMigrations` shipped in v1 with nothing to do, on the argument that a chain which exists is far
easier to extend than one introduced retroactively once saves are already in the wild.

It has since done two real jobs, and the difference between them is the useful part:

- **v1 → v2** (floor style and water level) needed **no step**. `JsonUtility` leaves an absent field at
  its default, and the default *is* what an older save means: it was built on the grid. The version
  was bumped anyway because the two are worth telling apart in a file.
- **v2 → v3** (the half-brick grid, §1.5) needed a real one. Every stored layer index now means half
  of what it did, so a creation saved before the change would load at half its height with its
  channels meeting nothing. **Doubling every layer is the whole migration** — the grid got finer, not
  different.

The rule that falls out: a step is for a change that cannot be read correctly without one. The `role`
field (start/goal, §6.5) was added with no bump at all, because "no role" is exactly what an older
save means by saying nothing.

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
  Art/Materials/                   // palette materials, one per colour (§5.2)
  Parts/Definitions/*.asset        // 37 PartDefinition SOs, mostly machine-derived
  Parts/PartCatalog.asset          // palette ordering + categories
  Scripts/
    Core/          GameMode state machine, bootstrap, physics config
    Grid/          GridCoord, GridMap, PlacedPart, PlacementSolver,
                   ScaffoldBuilder, Assembly, SelectionOps
    Parts/         PartDefinition, PartCatalog, PartFactory,
                   BrickColliderBuilder, PillarMeshBuilder, ProceduralPillars
    Build/         BuildController, GhostPreview, PartPalette, BuildHud,
                   Selection, PasteSession, AlignmentGuides, EditCommands,
                   SaveBrowser, UiScale, StressTest
    Track/         ChannelWelder, JointBridges, OpenPortMarkers
    Play/          PlayController, Marble, MarbleDefinition, MarbleAudio,
                   SoundBank, SoftPart, PhysicsPanel, RunTester
    CameraRig/     OrbitCamera, CameraDirector
    World/         BuildRaycaster, InfiniteGround, Scenery, Splash,
                   PlacedPartMarker, WebGLInputBootstrap
    Persistence/   SaveModel, SaveMigrations, SaveService,
                   ISaveStore, FileSaveStore, IndexedDbSaveStore
  Plugins/WebGL/   SaveStore.jslib
  Shaders/         InfiniteStudGrid.shader
  Editor/
    Import/        StlFile, StlMeshBuilder, StlScriptedImporter, PartAnalysis,
                   PartDefinitionGenerator, MirrorBuilder, PlateBuilder,
                   ObjToStl, PartReport, PortDiagnostic, OrientationDiagnostic
    Bootstrap/     SetupProject, BuildSceneSetup, BuildScript,
                   PartGalleryScene, PartIconBaker, AddPackages
    Tests/         GridSelfTest, SaveSelfTest, and the probes (§9.2)
```

Two things the plan expected that are not there. The **UI is IMGUI, not UI Toolkit** — the HUD is a
dense read-out of live state that is rewritten constantly, which is what IMGUI is good at, and a
retained-mode tree earned nothing here. Colliders are **built in code** (`BrickColliderBuilder`) rather
than authored as prefabs per part: the shapes are derivable from the same masks §3.5 derives, and a
prefab per part is one more thing to drift from the mesh.

Packages: URP 17.5.0 and Input System 1.20.0. That is the whole list.

### 9.1 Build & hosting matrix

Two WebGL hosts with different capabilities, so **two WebGL build configs**, not one compromise:

| | Self-hosted | GitHub Pages |
|---|---|---|
| Can set `Content-Encoding: br` | yes | **no** — serves files as-is |
| Compression | Brotli, fallback **off** | Brotli, fallback **on** |
| Cost | smallest + fastest load | `.loader.js` ships a JS decompressor; slower startup, ~same transfer size |

GitHub Pages cannot set response headers, so without the decompression fallback the browser receives Brotli bytes it was never told to decode and the build fails to load. The fallback is the price of Pages hosting — enable it there, not everywhere.

Ship both from one CI job (`unity-builder` GitHub Action): `webgl-selfhost`, `webgl-pages` (auto-deployed to Pages on green), `macos` (Apple Silicon / Universal, Metal). Pages doubles as the always-current playable link for testing on other machines.

**CI runs on a self-hosted runner, on demand.** The hosted path is not merely unconfigured, it is
closed: `game-ci/unity-builder` runs the editor in Docker and needs a licence in the repository
secrets, and there is no licence to give it. Unity has retired manual activation for Personal —
`license.unity3d.com/manual` now accepts a Plus/Pro serial and nothing else — and a Unity 6 Personal
entitlement has neither a serial nor a `.ulf` on disk; it lives as an entitlement XML tied to the
account. GameCI's own `unity-request-activation-file` action is retired and answers any run with "this
action is no longer supported".

Every push failing in twenty seconds for that reason was worse than no signal, so the automatic
triggers came off. What replaced them: the machine that already has an activated editor does the
building, through a `workflow_dispatch`-only workflow on a self-hosted runner registered to this
repository. No secrets, no Docker, no licence handling. The runner is not installed as a service —
it is started by hand when a build is wanted — and its workspace persists, so `Library/` stays warm
without a cache step, which was the largest cost of the hosted version.

What this gives up is builds on someone else's machine. What it keeps is the point of §10's rule: a
WebGL build that can be run at any time, plus a deployed Pages build to hand someone.

**A second lesson, learned the expensive way.** A self-hosted runner fetches every action it uses from
`codeload.github.com` at the start of every job — `checkout`, `upload-artifact`, the Pages pair — where
a hosted runner has them baked into its image. An afternoon of builds got the machine's address rate
limited, and then jobs failed in *setup*, before Unity was reached, with nothing wrong in the project
at all. The workflow now uses no actions whatever: `git` fetches the source and `git` pushes the
Pages build to a `gh-pages` branch. What that costs is uploaded artifacts, which for builds that
already live on the machine that made them is close to nothing.

**One rule comes with it.** The repository is public — it has to be, for Pages to deploy from a free
account — and a public repository with a self-hosted runner is the case GitHub warns about: anyone can
fork and open a pull request, and a workflow that starts by itself would run their code on the
author's machine. `workflow_dispatch` is the guard, because only someone with write access can start
it. So the triggers stay manual for as long as the runner is the author's Mac, and restoring automatic
builds means moving to a hosted runner first, which means solving the licence problem above.

**Common settings**: IL2CPP, Managed Stripping High, unused engine modules stripped, `link.xml` preserving save-model types (§11).

### 9.2 Headless probes beat reasoning from screenshots

The `Editor/Tests` folder holds two self-tests and a dozen small probes — `ScaffoldProbe`,
`PillarProbe`, `SocketProbe`, `GuideProbe`, `ColliderProbe`, `LevelProbe`, `MeshProbe`,
`PartRenderProbe` — each of which sets up one situation headlessly and prints the numbers.

They are listed here because the pattern was worth more than any one of them. Nearly every stubborn
bug in this project was settled in a single run of a probe after rounds of reasoning from screenshots
had got it wrong: the mirror masks that were never copied, the wheel step being consumed twice a
frame, the funnel's real footprint, an STL that imported inside-out (found by rendering it to a PNG
and looking at it), the antistud tolerance measured as 64 samples at exactly 0.0 against a scatter.

`PartMaskReport` (§3.5) is the same idea aimed at the author rather than at the code.


---

## 10. Milestones

| # | Goal | Done when |
|---|---|---|
| M0 | Project + import pipeline | **Done.** Unity 6.5 URP project, STL importer, all parts at correct scale with smooth normals, chirality analysis + mirror generation reviewed. CI written on day one but disabled since (§9.1) |
| M1 | Grid & placement | **Done.** Orbit camera, infinite grid ground, ghost preview, place/delete with snapping, multi-layer parts, support rules, 2000-part stress scene measured (§5.2) |
| M2 | Editing | **Done.** Rotation, colours, box select, undo/redo, async save/load via `ISaveStore` on both targets, thumbnails |
| M3 | Track + supports | **Done.** Derived ports, channel-to-channel clutch, open-mouth feedback, start & goal designation, build-time auto-scaffolding (§5.1) |
| M4 | Play | **Done.** Mode switch, marble physics tuned per §2, release/reset, out-of-bounds, goal detection, timer, channel welding (§7.1) |
| M5 | Feel | **Done.** Palette (IMGUI, §9), sound, splash particles, camera follow with four views (§7.4), sand and water floors (§7.2), sky and fog, alignment guides (§5.4), precise placement (§5.3), autosave (§5.6), save browser with thumbnails |
| M6 | Content | Challenge/level mode, sandbox scoring, more STL parts, share codes + file import/export |

M0–M2 give a usable brick editor; M4 is the first genuinely fun build. Ship-quality vertical slice = M0–M4.

**Where M4/M5 landed differently from the plan.** Soft assist (§13) is *not* built: the marbles ran
well enough on physics alone once the channel seams were welded away (§7.1), and the assist's whole
justification was rescuing marginal runs. It stays designed and unbuilt, which is the right state for
something whose trigger has not fired. The M5 list grew instead in the direction the building itself
demanded — guides, precise placement, autosave — which is what happens when a tool is used rather than
specified.

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
| Draw calls at large builds | **Measured (§5.2)**: per-brick `MaterialPropertyBlock` cost 20 ms vs 13 ms at 2000 parts by breaking SRP batching. Palette materials instead; `RenderMeshInstanced` held in reserve |
| Per-instance colour silently disabling batching again | Colour comes from shared palette materials, never a property block, on any path that touches many renderers |
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
| **A derived mask silently not copied to a mirror** | Bit the project three times (`layerMasks`, `hasTunnel`, `bottomSockets`), each showing up as odd behaviour far downstream. Self-test asserts the mirrored masks; `PartMasks.txt` (§3.5) makes them readable |
| **A tolerance that is loose enough to be wrong** | Socket flatness measured, not chosen: 0.15 mm, from a real antistud sampling at exactly 0.0 against a ramp's 19–21 mm scatter (§3.5) |
| **A ball catching on the seam between two colliders** | Weld joined channel runs into one collider on entering play (§7.1). Bridging seams adds seams |
| CI red on every commit for a reason unrelated to the commit | Automatic triggers off until the Unity licence secrets exist (§9.1); a permanently red badge trains everyone to ignore it |
| Autosave overwriting the work it is meant to protect | Opening state counts as already saved; never write an empty build over a non-empty one (§5.6) |
| IMGUI and the Input System both reading the same click | The palette marks a click used; the world checks before acting on it. Two independent readers of one event is the bug, not the symptom |
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

**Settled since**

8. ~~Vertical grid step~~ → **half a brick (9.6 mm)**, so plates and the 1.5-brick funnels have somewhere to stand. Save v3 doubles stored layers. See §1.5, §8.0.
9. ~~Supports~~ → **real bricks at placement time**, not scaffolding conjured on the switch to play; pillars are procedural and re-cut when raised. See §5.1, §3.7.
10. ~~UI toolkit~~ → **IMGUI**, not UI Toolkit. The HUD is a live read-out, not a retained tree. See §9.
11. ~~Soft assist~~ → **designed, not built.** Welding the channel seams (§7.1) removed the failure it existed to rescue. Revisit if playtesting with young children says otherwise. See §13.
12. ~~CI~~ → **disabled until the Unity licence secrets exist**, workflow kept intact. See §9.1.

All decisions settled. Nothing blocking.

---

## 13. Decided: soft assist (option C) — designed, not built

**Mixed-age audience → rigidbody physics as the truth, plus a tunable corrective force.** Sections 13.1–13.3 record the alternatives and why C won.

**Nothing here is implemented.** The design stands, and the reason it has not been needed is worth
recording: the marble misbehaviour that motivated it turned out to be mostly the collider seams
between joined track pieces (§7.1), not the physics. With those welded away, marbles run the built
tracks on gravity alone. `assistStrength = 0` was always the shipping default, so the unbuilt state is
the designed state — the slider simply does not exist yet. Build it if playtesting with a young child
says the run still needs rescuing; the `centerline` field is already on `PartDefinition` and empty.

### 13.0 What it would be

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

**The revisit trigger has now fired and come back negative.** The M1 stress test (§5.2) cut triangles fourfold across 2000 parts and measured *no* change in frame time — the frame is CPU-bound on draw submission, and the GPU was comfortable at 16.7 M triangles. LODs would have bought nothing.

Worth recording as a near miss: the triangle counts *look* alarming (8328 for a 2×2 brick, 22k for a 2×10) and the throughput figure alone was consistent with being geometry-bound. Building the Blender pass on that reasoning would have added a toolchain dependency, cost the drop-in property, and fixed nothing. The sparse-mesh variant took one keypress to run and settled it.

**New trigger**: if a future profile shows geometry cost rising above draw-submission cost — most likely from far higher part counts, or from shadow passes multiplying the geometry — re-run the sparse-mesh comparison first, and only then reach for Blender.

If it does get added, keep it a CI step that reads `Art/Meshes/*.stl` and writes `Art/Meshes/Generated/*_lod1.mesh`, so the drop-in property survives.
