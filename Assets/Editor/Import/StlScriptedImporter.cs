#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Import
{
    /// <summary>
    /// Imports .stl straight from the source folder, so extending the part set is a matter of
    /// dropping a file in rather than running a conversion step. See DESIGN.md section 3.
    ///
    /// The mesh is the main asset: every consumer here (PartDefinition, the chirality analyser,
    /// the validator window) wants a Mesh, and making it the main asset keeps assignment a
    /// single drag.
    /// </summary>
    [ScriptedImporter(Version, "stl")]
    public sealed class StlScriptedImporter : ScriptedImporter
    {
        /// <summary>Bump to force Unity to re-run the importer over every .stl after a logic change.</summary>
        const int Version = 18;

        /// <summary>
        /// Source STLs are in millimetres. 0.01 puts the project at 1 unit = 10 cm, which keeps a
        /// 13 mm marble inside PhysX's usable range - see DESIGN.md section 2 for why real metric
        /// scale does not work here.
        /// </summary>
        [Tooltip("Millimetres to world units. 0.01 gives 1 unit = 10 cm (see DESIGN.md §2).")]
        public float scale = 0.01f;

        [Tooltip("Faces meeting at a sharper angle than this keep separate normals, so hard edges stay crisp.")]
        [Range(0f, 180f)]
        public float smoothingAngle = 30f;

        [Tooltip("Leave off unless a MeshCollider needs this mesh at runtime; readable meshes keep a CPU copy, which WebGL cannot spare.")]
        public bool readable;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            List<StlFacet> facets = StlFile.Read(ctx.assetPath);

            if (facets.Count == 0)
            {
                ctx.LogImportError($"No triangles found in '{ctx.assetPath}'. File may be truncated or not an STL.");
                return;
            }

            string meshName = System.IO.Path.GetFileNameWithoutExtension(ctx.assetPath);
            Mesh mesh = StlMeshBuilder.Build(facets, scale, smoothingAngle, meshName);

            // Cross-check the winding decision against the mesh's own geometry before the CPU copy is
            // discarded. An inverted import is invisible in the numbers and only shows up as a part
            // rendering inside out, so it needs to fail loudly here rather than wait to be spotted
            // by eye. Near-zero means the mesh is not closed, in which case volume proves nothing.
            double volume = StlMeshBuilder.SignedVolume(mesh);
            if (volume < 0.0 && System.Math.Abs(volume) > 1e-9)
            {
                ctx.LogImportError(
                    $"'{meshName}' imported inside out (signed volume {volume:0.######}). " +
                    "The winding vote disagreed with the geometry; check the facet normals in the source file.");
            }

            mesh.UploadMeshData(!readable);

            ctx.AddObjectToAsset("mesh", mesh);
            ctx.SetMainObject(mesh);
        }
    }
}
#endif
