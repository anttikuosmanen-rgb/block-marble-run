#if UNITY_EDITOR
using System.IO;
using System.Text;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Import
{
    /// <summary>
    /// Creates or refreshes a <see cref="PartDefinition"/> for every source STL, and generates the
    /// mirror counterpart for parts the analyser calls chiral (DESIGN.md §3.2, §3.4).
    ///
    /// Only derived fields are written. Anything a human authored - ports, centrelines, category
    /// overrides, a reviewed mirror verdict - survives a re-run, so this is safe to invoke whenever
    /// new STLs are dropped in.
    /// </summary>
    public static class PartDefinitionGenerator
    {
        const string MeshFolder = "Assets/Art/Meshes";
        const string GeneratedFolder = "Assets/Art/Meshes/Generated";
        const string DefinitionFolder = "Assets/Parts/Definitions";

        /// <summary>
        /// Re-derives every mirror verdict from scratch, discarding stored ones, and removes mirror
        /// assets that are no longer justified. Needed whenever the chirality analysis itself changes:
        /// the normal path deliberately preserves a stored verdict, which would otherwise keep four
        /// wrongly-generated mirrors in the palette forever.
        /// </summary>
        [MenuItem("Block Marble Run/Reanalyse Mirrors")]
        public static void Reanalyse() => Run(forceReanalyse: true);

        [MenuItem("Block Marble Run/Generate Part Definitions")]
        public static void Run() => Run(forceReanalyse: false);

        static void Run(bool forceReanalyse)
        {
            Directory.CreateDirectory(GeneratedFolder);
            Directory.CreateDirectory(DefinitionFolder);

            string[] paths = Directory.GetFiles(MeshFolder, "*.stl", SearchOption.TopDirectoryOnly);
            System.Array.Sort(paths);

            var log = new StringBuilder();
            int created = 0, updated = 0, mirrors = 0, plates = 0, needsReview = 0;

            foreach (string stlPath in paths)
            {
                string name = Path.GetFileNameWithoutExtension(stlPath);
                // A stalk is solid for one layer and leaves above that. Measured whole it claims a
                // two-by-two, because its fronds spread that far - and four of them will not go into
                // one brick, which is exactly what they are for.
                bool isSoft = name.StartsWith("stalk");

                PartAnalysis analysis = PartAnalysis.Analyse(
                    stlPath, isSoft ? PartAnalysis.LayerHeightMm : 0f);

                PartDefinition def = LoadOrCreate(name, ref created, ref updated);

                if (forceReanalyse)
                    def.mirrorVerdict = MirrorVerdict.Unreviewed;

                ApplyDerived(def, analysis, name);
                EnsureReadableIfChannel(stlPath, analysis);
                def.mesh = AssetDatabase.LoadAssetAtPath<Mesh>(stlPath);
                EditorUtility.SetDirty(def);

                // Bricks get a half-height twin. The grid steps half a brick, so a column one layer
                // short of its load has nothing else that fits, and a part meeting a channel halfway
                // needs somewhere to stand.
                if (name.StartsWith("building_block") && GeneratePlate(stlPath, name, def))
                    plates++;

                if (def.mirrorVerdict == MirrorVerdict.Chiral)
                {
                    GenerateMirror(stlPath, name, def, analysis);
                    mirrors++;
                }
                else
                {
                    // A part that is no longer chiral must lose its mirror, or the palette keeps
                    // offering a duplicate that nothing regenerates and nobody notices.
                    if (DeleteMirror(name))
                        log.AppendLine($"  removed stale mirror for {name} (score {analysis.MirrorScore:0.00})");

                    if (def.mirrorVerdict == MirrorVerdict.Ambiguous)
                    {
                        needsReview++;
                        log.AppendLine($"  REVIEW {name}: mirror score {analysis.MirrorScore:0.00} " +
                                       "sits between the thresholds. Open the part and set mirrorVerdict by hand.");
                    }
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[Parts] {created} created, {updated} refreshed, {mirrors} mirrors and " +
                      $"{plates} plates generated, {needsReview} awaiting review.\n{log}");
        }

        /// <summary>
        /// Channel meshes keep their CPU copy; everything else stays GPU-only.
        ///
        /// A MeshCollider is cooked at runtime and needs to read the mesh, so a channel part cannot be
        /// uploaded non-readable. Bricks can, and they are the heavy ones - up to 22k triangles each
        /// against 2-5k for track - so confining readability to the parts that need it keeps most of
        /// the geometry off the WebGL heap.
        /// </summary>
        static void EnsureReadableIfChannel(string stlPath, PartAnalysis analysis)
        {
            if (AssetImporter.GetAtPath(stlPath) is not StlScriptedImporter importer)
                return;

            // Pillars too: their mesh is the template a support column of any height is cut from, so
            // it has to be readable at runtime even though nothing collides against the original.
            // Soft parts too: their mesh is bent at runtime, which means reading it.
            string name = System.IO.Path.GetFileNameWithoutExtension(stlPath);

            bool wanted = analysis.Ports.Count > 0 || analysis.HasTunnel ||
                          name.StartsWith("pillar") || name.StartsWith("stalk");
            if (importer.readable == wanted)
                return;

            importer.readable = wanted;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
        }

        static PartDefinition LoadOrCreate(string name, ref int created, ref int updated)
        {
            string path = $"{DefinitionFolder}/{name}.asset";
            var def = AssetDatabase.LoadAssetAtPath<PartDefinition>(path);

            if (def != null)
            {
                updated++;
                return def;
            }

            def = ScriptableObject.CreateInstance<PartDefinition>();
            def.id = name;
            def.displayName = Prettify(name);
            def.category = GuessCategory(name);

            // Set once, at creation, like the category - so a decision made by hand afterwards is
            // not overwritten on the next import.
            def.soft = name.StartsWith("stalk");

            if (def.soft)
            {
                // Solid for one layer - the stem - and green. A plant comes out green whatever
                // colour the player happens to be building in; painting it afterwards still works.
                def.softBodyLayers = 1;
                def.defaultColorIndex = 3;

                // Half a stud up. The flanges around its underside straddle the stud rather than
                // perching on top of it, so it settles about halfway down.
                def.verticalOffsetUnits = PartAnalysis.StudHeightMm * 0.5f * 0.01f;
            }

            AssetDatabase.CreateAsset(def, path);
            created++;
            return def;
        }

        /// <summary>
        /// Writes only what the analyser owns. The mirror verdict is written once and then left
        /// alone - re-deriving it every run would overwrite a human's review with a guess, which is
        /// exactly the failure DESIGN.md §3.4 sets out to avoid.
        /// </summary>
        static void ApplyDerived(PartDefinition def, PartAnalysis analysis, string name)
        {
            def.footprintSize = analysis.FootprintSize;
            def.footprintMask = analysis.FootprintMask;
            def.heightLayers = analysis.HeightLayers;
            def.pivotOffsetUnits = analysis.PivotOffsetUnits;
            def.hasTunnel = analysis.HasTunnel;

            // Relative to the pivot, because that is what the part is drawn around: a mesh point p
            // lands at footprintCentre + rotation * (p - pivot), so storing the difference makes the
            // guides' arithmetic the same rotation the piece itself gets.
            def.dropHoleOffsetUnits = analysis.DropHoleRadiusMm > 0f
                ? analysis.DropHoleCentreMm * 0.01f - analysis.PivotOffsetUnits
                : Vector2.zero;

            def.dropHoleRadiusUnits = analysis.DropHoleRadiusMm * 0.01f;

            // What a channel feeding this part has to climb to meet it (channelLipUnits). Nothing is
            // moved here: the lift goes on the run at build time, in ChannelNetwork.
            def.channelLipUnits = analysis.ColliderDropMm * 0.01f;
            def.layerMasks = analysis.LayerMasks;
            def.topStuds = analysis.TopStuds;

            // Ports are derived, not authored: the geometry states them unambiguously (DESIGN.md §6),
            // and hand-entered coordinates would drift from the mesh the first time a part changes.
            def.ports = analysis.Ports.ToArray();

            // Derived from the underside, not copied from the footprint. The copy claimed a socket
            // under every cell a part covered, which is true of a brick and false of half of anything
            // with a channel: a slide's underside is the back of its own groove and a tunnel's is its
            // roof, and a support pillar built up into either blocks the thing it is there to carry.
            def.bottomSockets = analysis.BottomSockets;

            if (def.mirrorVerdict == MirrorVerdict.Unreviewed)
                def.mirrorVerdict = analysis.MirrorVerdict;

            foreach (string warning in analysis.Warnings)
                Debug.LogWarning($"[Parts] {name}: {warning}", def);
        }

        /// <summary>
        /// Writes the half-height twin of a brick, mesh and definition together.
        ///
        /// Everything except the height is the brick's own: the same footprint, the same studs and
        /// the same sockets, because a plate is a brick with less wall between them.
        /// </summary>
        static bool GeneratePlate(string stlPath, string sourceName, PartDefinition source)
        {
            string plateName = $"{sourceName}_plate";
            string meshPath = $"{GeneratedFolder}/{plateName}.asset";

            var importer = AssetImporter.GetAtPath(stlPath) as StlScriptedImporter;
            float scale = importer != null ? importer.scale : 0.01f;
            float smoothing = importer != null ? importer.smoothingAngle : 30f;

            Mesh mesh = PlateBuilder.BuildMesh(stlPath, scale, smoothing, plateName);

            if (mesh == null)
            {
                Debug.LogWarning($"[Parts] '{sourceName}' has no plain wall to shorten; no plate made.");
                return false;
            }

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (existing != null)
            {
                existing.Clear();
                existing.indexFormat = mesh.indexFormat;
                existing.SetVertices(mesh.vertices);
                existing.SetNormals(mesh.normals);
                existing.SetTriangles(mesh.triangles, 0);
                existing.RecalculateBounds();
                mesh = existing;
                EditorUtility.SetDirty(existing);
            }
            else
            {
                AssetDatabase.CreateAsset(mesh, meshPath);
            }

            string defPath = $"{DefinitionFolder}/{plateName}.asset";
            var def = AssetDatabase.LoadAssetAtPath<PartDefinition>(defPath);

            if (def == null)
            {
                def = ScriptableObject.CreateInstance<PartDefinition>();
                AssetDatabase.CreateAsset(def, defPath);
            }

            def.id = plateName;
            def.displayName = $"{source.displayName} plate";
            def.category = source.category;
            def.mesh = mesh;

            def.footprintSize = source.footprintSize;
            def.footprintMask = source.footprintMask;
            def.topStuds = source.topStuds;
            def.bottomSockets = source.bottomSockets;
            def.pivotOffsetUnits = source.pivotOffsetUnits;
            def.rotation = source.rotation;
            def.hasTunnel = source.hasTunnel;
            def.dropHoleOffsetUnits = source.dropHoleOffsetUnits;
            def.dropHoleRadiusUnits = source.dropHoleRadiusUnits;
            def.channelLipUnits = source.channelLipUnits;
            def.ports = source.ports;
            def.centerline = source.centerline;
            // A plate is as handed as the brick it came from, which is to say not at all - and
            // marking it Redundant keeps the generator from ever making a mirror of one.
            def.mirrorVerdict = MirrorVerdict.Redundant;

            // Half of the brick, and solid, so the default full-prism occupancy is the truth.
            def.heightLayers = Mathf.Max(1, source.heightLayers / 2);
            def.layerMasks = null;

            // Only the 2x2 goes on the bar. The others are built by the scaffolder and named in save
            // files, so they have to exist - they just do not each need a slot on a palette that has
            // to fit on screen.
            def.selectable = source.footprintSize is { x: 2, y: 2 };

            EditorUtility.SetDirty(def);
            return true;
        }

        static void GenerateMirror(string stlPath, string sourceName, PartDefinition source, PartAnalysis analysis)
        {
            string mirrorName = $"{sourceName}_mirror";
            string meshPath = $"{GeneratedFolder}/{mirrorName}.asset";

            var importer = AssetImporter.GetAtPath(stlPath) as StlScriptedImporter;
            float scale = importer != null ? importer.scale : 0.01f;
            float smoothing = importer != null ? importer.smoothingAngle : 30f;

            Mesh mesh = MirrorBuilder.BuildMesh(stlPath, scale, smoothing, mirrorName);

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (existing != null)
            {
                // Overwrite in place so anything already referencing this mesh keeps its reference.
                existing.Clear();
                existing.indexFormat = mesh.indexFormat;
                existing.SetVertices(mesh.vertices);
                existing.SetNormals(mesh.normals);
                existing.SetTriangles(mesh.triangles, 0);
                existing.RecalculateBounds();
                mesh = existing;
                EditorUtility.SetDirty(existing);
            }
            else
            {
                AssetDatabase.CreateAsset(mesh, meshPath);
            }

            string defPath = $"{DefinitionFolder}/{mirrorName}.asset";
            var def = AssetDatabase.LoadAssetAtPath<PartDefinition>(defPath);
            if (def == null)
            {
                def = ScriptableObject.CreateInstance<PartDefinition>();
                AssetDatabase.CreateAsset(def, defPath);
            }

            def.id = mirrorName;
            def.displayName = $"{source.displayName} (mirrored)";
            def.category = source.category;
            def.mirrorOf = source.id;
            def.mirrorVerdict = MirrorVerdict.Chiral;
            def.mesh = mesh;
            def.heightLayers = source.heightLayers;
            def.pivotOffsetUnits = new Vector2(-source.pivotOffsetUnits.x, source.pivotOffsetUnits.y);
            def.footprintSize = source.footprintSize;
            def.rotation = source.rotation;

            // Not handed, and easy to leave out - which is how three mirrored parts ended up with a
            // solid box where their tunnel should be. hasTunnel is what routes a part to its own
            // geometry; without it the factory falls through to the generated brick collider, and a
            // funnel's bowl or a slide's underpass is filled in solid.
            def.hasTunnel = source.hasTunnel;

            // Mirrored the same way the pivot is, and for the same reason it must not be forgotten:
            // a hole drawn on the wrong side of a mirrored funnel points at the one place the ball
            // will not go.
            def.dropHoleOffsetUnits = new Vector2(-source.dropHoleOffsetUnits.x,
                                                  source.dropHoleOffsetUnits.y);
            def.dropHoleRadiusUnits = source.dropHoleRadiusUnits;
            def.channelLipUnits = source.channelLipUnits;


            def.footprintMask = MirrorBuilder.MirrorMask(source.footprintMask, source.footprintSize);
            def.layerMasks = MirrorBuilder.MirrorLayerMasks(source.layerMasks, source.footprintSize,
                                                            Mathf.Max(1, source.heightLayers));
            def.topStuds = MirrorBuilder.MirrorMask(source.topStuds, source.footprintSize);
            def.bottomSockets = MirrorBuilder.MirrorMask(source.bottomSockets, source.footprintSize);
            def.ports = MirrorBuilder.MirrorPorts(source.ports, source.footprintSize);
            def.centerline = MirrorBuilder.MirrorCenterline(source.centerline);

            EditorUtility.SetDirty(def);
        }

        /// <summary>Removes a generated mirror pair. Returns true if anything was there to remove.</summary>
        static bool DeleteMirror(string sourceName)
        {
            string mirrorName = $"{sourceName}_mirror";
            bool removed = false;

            foreach (string path in new[]
                     {
                         $"{DefinitionFolder}/{mirrorName}.asset",
                         $"{GeneratedFolder}/{mirrorName}.asset",
                     })
            {
                if (AssetDatabase.LoadAssetAtPath<Object>(path) != null)
                    removed |= AssetDatabase.DeleteAsset(path);
            }

            return removed;
        }

        static PartCategory GuessCategory(string name)
        {
            if (name.StartsWith("building_block") || name.StartsWith("pillar")) return PartCategory.Block;
            if (name.StartsWith("funnel")) return PartCategory.Slide;
            if (name.StartsWith("bridge")) return PartCategory.Bridge;
            if (name.StartsWith("crossing")) return PartCategory.Crossing;
            if (name.StartsWith("terminal")) return PartCategory.Terminal;
            if (name.Contains("slide")) return PartCategory.Slide;
            if (name.Contains("curve") || name.StartsWith("u_turn")) return PartCategory.Curve;
            return PartCategory.Track;
        }

        static string Prettify(string name) =>
            System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.Replace('_', ' '));
    }
}
#endif
