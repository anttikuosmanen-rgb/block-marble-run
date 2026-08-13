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
            int created = 0, updated = 0, mirrors = 0, needsReview = 0;

            foreach (string stlPath in paths)
            {
                string name = Path.GetFileNameWithoutExtension(stlPath);
                PartAnalysis analysis = PartAnalysis.Analyse(stlPath);

                PartDefinition def = LoadOrCreate(name, ref created, ref updated);

                if (forceReanalyse)
                    def.mirrorVerdict = MirrorVerdict.Unreviewed;

                ApplyDerived(def, analysis, name);
                def.mesh = AssetDatabase.LoadAssetAtPath<Mesh>(stlPath);
                EditorUtility.SetDirty(def);

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

            Debug.Log($"[Parts] {created} created, {updated} refreshed, {mirrors} mirrors generated, " +
                      $"{needsReview} awaiting review.\n{log}");
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
            def.topStuds = analysis.TopStuds;

            if (def.bottomSockets == null || def.bottomSockets.Length != analysis.FootprintMask.Length)
                def.bottomSockets = (bool[])analysis.FootprintMask.Clone();

            if (def.mirrorVerdict == MirrorVerdict.Unreviewed)
                def.mirrorVerdict = analysis.MirrorVerdict;

            foreach (string warning in analysis.Warnings)
                Debug.LogWarning($"[Parts] {name}: {warning}", def);
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
            def.footprintSize = source.footprintSize;
            def.rotation = source.rotation;

            def.footprintMask = MirrorBuilder.MirrorMask(source.footprintMask, source.footprintSize);
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
            if (name.StartsWith("building_block")) return PartCategory.Block;
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
