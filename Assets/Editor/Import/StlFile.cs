#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Import
{
    /// <summary>A single STL facet: three corners plus the normal the exporter wrote (may be zero).</summary>
    public struct StlFacet
    {
        public Vector3 A, B, C;
        public Vector3 Normal;
    }

    /// <summary>
    /// Raw STL reader. Returns facets in the file's own coordinate system (CAD millimetres, Z-up)
    /// with no scaling or axis conversion applied - that is the importer's job (<see cref="StlScriptedImporter"/>),
    /// so this stays a pure parser and can be unit tested against known files.
    /// </summary>
    public static class StlFile
    {
        const int BinaryHeaderBytes = 80;
        const int BinaryFacetBytes = 50;

        public static List<StlFacet> Read(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            return IsBinary(bytes) ? ReadBinary(bytes) : ReadAscii(bytes);
        }

        /// <summary>
        /// An ASCII STL may still begin with the word "solid", so sniffing the header is not enough.
        /// The reliable test is whether the declared triangle count exactly accounts for the file
        /// length: binary STL is a fixed-stride format, so the arithmetic only works out for real
        /// binary files.
        /// </summary>
        static bool IsBinary(byte[] bytes)
        {
            if (bytes.Length < BinaryHeaderBytes + 4)
                return false;

            uint count = BitConverter.ToUInt32(bytes, BinaryHeaderBytes);
            long expected = BinaryHeaderBytes + 4L + (long)count * BinaryFacetBytes;
            return expected == bytes.Length;
        }

        static List<StlFacet> ReadBinary(byte[] bytes)
        {
            int count = (int)BitConverter.ToUInt32(bytes, BinaryHeaderBytes);
            var facets = new List<StlFacet>(count);

            int offset = BinaryHeaderBytes + 4;
            for (int i = 0; i < count; i++)
            {
                facets.Add(new StlFacet
                {
                    Normal = ReadVector(bytes, offset),
                    A = ReadVector(bytes, offset + 12),
                    B = ReadVector(bytes, offset + 24),
                    C = ReadVector(bytes, offset + 36),
                });
                offset += BinaryFacetBytes; // 48 bytes of floats + 2 byte attribute count
            }

            return facets;
        }

        static Vector3 ReadVector(byte[] bytes, int offset) => new Vector3(
            BitConverter.ToSingle(bytes, offset),
            BitConverter.ToSingle(bytes, offset + 4),
            BitConverter.ToSingle(bytes, offset + 8));

        static List<StlFacet> ReadAscii(byte[] bytes)
        {
            var facets = new List<StlFacet>();
            var corners = new List<Vector3>(3);
            Vector3 normal = Vector3.zero;

            using var reader = new StringReader(Encoding.ASCII.GetString(bytes));
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string[] token = line.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (token.Length == 0)
                    continue;

                switch (token[0])
                {
                    case "facet" when token.Length >= 5 && token[1] == "normal":
                        normal = ParseVector(token, 2);
                        corners.Clear();
                        break;

                    case "vertex" when token.Length >= 4:
                        corners.Add(ParseVector(token, 1));
                        break;

                    case "endfacet" when corners.Count == 3:
                        facets.Add(new StlFacet { Normal = normal, A = corners[0], B = corners[1], C = corners[2] });
                        break;
                }
            }

            return facets;
        }

        static Vector3 ParseVector(string[] token, int start) => new Vector3(
            float.Parse(token[start], CultureInfo.InvariantCulture),
            float.Parse(token[start + 1], CultureInfo.InvariantCulture),
            float.Parse(token[start + 2], CultureInfo.InvariantCulture));
    }
}
#endif
