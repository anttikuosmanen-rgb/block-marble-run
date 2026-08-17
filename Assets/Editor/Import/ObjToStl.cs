#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Import
{
    /// <summary>
    /// Brings a Lego-scale OBJ in as a Duplo-scale STL.
    ///
    /// Duplo is Lego doubled in every dimension - 16 mm between studs against 8, 19.2 mm of brick
    /// against 9.6 - so a model of a Lego part becomes the Duplo part of the same shape by scaling
    /// it by two. Stud counts and brick heights carry over unchanged, which is why this is worth
    /// doing at all rather than remodelling.
    ///
    /// Written out as STL because that is what the part pipeline reads: the analyser, the mirror
    /// generator and the plate cutter all work from facets, and an OBJ imported directly by Unity
    /// would be a mesh with none of that around it.
    /// </summary>
    public static class ObjToStl
    {
        [MenuItem("Block Marble Run/Convert OBJ to Duplo STL")]
        public static void Run()
        {
            string source = System.Environment.GetEnvironmentVariable("BMR_OBJ")
                ?? EditorUtility.OpenFilePanel("Lego-scale OBJ", "", "obj");

            if (string.IsNullOrEmpty(source) || !File.Exists(source))
            {
                Debug.Log("[Obj] no file chosen");
                return;
            }

            string name = System.Environment.GetEnvironmentVariable("BMR_OBJ_NAME")
                ?? Path.GetFileNameWithoutExtension(source);

            float scale = float.TryParse(System.Environment.GetEnvironmentVariable("BMR_OBJ_SCALE"),
                NumberStyles.Float, CultureInfo.InvariantCulture, out float given) ? given : 2f;

            var vertices = new List<Vector3>();
            var triangles = new List<(int A, int B, int C)>();

            foreach (string line in File.ReadLines(source))
            {
                if (line.StartsWith("v "))
                {
                    string[] parts = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);

                    vertices.Add(new Vector3(
                        float.Parse(parts[1], CultureInfo.InvariantCulture),
                        float.Parse(parts[2], CultureInfo.InvariantCulture),
                        float.Parse(parts[3], CultureInfo.InvariantCulture)));
                }
                else if (line.StartsWith("f "))
                {
                    string[] parts = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                    var corners = new List<int>(parts.Length - 1);

                    for (int i = 1; i < parts.Length; i++)
                        corners.Add(int.Parse(parts[i].Split('/')[0], CultureInfo.InvariantCulture) - 1);

                    // Fanned, so a quad or an n-gon comes through as triangles.
                    for (int i = 1; i + 1 < corners.Count; i++)
                        triangles.Add((corners[0], corners[i], corners[i + 1]));
                }
            }

            if (triangles.Count == 0)
            {
                Debug.LogError($"[Obj] '{source}' has no faces.");
                return;
            }

            string destination = $"Assets/Art/Meshes/{name}.stl";
            Write(destination, vertices, triangles, scale);

            AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceUpdate);
            Debug.Log($"[Obj] {triangles.Count} triangles at {scale}x -> {destination}");
        }

        static void Write(string path, List<Vector3> vertices, List<(int A, int B, int C)> triangles, float scale)
        {
            using var stream = new FileStream(path, FileMode.Create);
            using var writer = new BinaryWriter(stream);

            writer.Write(new byte[80]);
            writer.Write(triangles.Count);

            foreach ((int a, int b, int c) in triangles)
            {
                // B and C swapped. Turning the model upright exchanges two axes, and exchanging two
                // axes mirrors the space - every triangle comes out wound the other way round, and
                // the whole part imports inside out. Swapping two corners puts the winding back.
                Vector3 A = Convert(vertices[a], scale);
                Vector3 B = Convert(vertices[c], scale);
                Vector3 C = Convert(vertices[b], scale);

                Vector3 normal = Vector3.Cross(B - A, C - A);
                normal = normal.sqrMagnitude > 1e-12f ? normal.normalized : Vector3.up;

                foreach (Vector3 v in new[] { normal, A, B, C })
                {
                    writer.Write(v.x);
                    writer.Write(v.y);
                    writer.Write(v.z);
                }

                writer.Write((ushort)0);
            }
        }

        /// <summary>
        /// Scaled, and turned upright. OBJ models are usually y-up while STL is z-up, and the part
        /// pipeline reads heights off z - an unturned model imports lying on its side, with its studs
        /// pointing at the neighbouring piece.
        /// </summary>
        static Vector3 Convert(Vector3 v, float scale) => new(v.x * scale, v.z * scale, v.y * scale);
    }
}
#endif
