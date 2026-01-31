using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

public static class StlImporter
{
    public static bool TryLoad(byte[] bytes, bool swapYAndZ, out Mesh mesh, out string error)
    {
        mesh = null;
        error = null;

        if (bytes == null || bytes.Length < 84)
        {
            error = "STL too small.";
            return false;
        }

        // Prefer binary STL when header doesn't look like ASCII.
        if (LooksLikeBinaryStl(bytes))
        {
            return TryLoadBinary(bytes, swapYAndZ, out mesh, out error);
        }

        // Otherwise try ASCII.
        return TryLoadAscii(bytes, swapYAndZ, out mesh, out error);
    }

    private static bool LooksLikeBinaryStl(byte[] bytes)
    {
        // Binary STL: 80-byte header, then uint32 triangle count, then 50 bytes per triangle.
        // ASCII STL starts with "solid". But binary headers can also start with "solid", so validate length.
        if (bytes.Length < 84) return false;

        var triCount = BitConverter.ToUInt32(bytes, 80);
        var expected = 84L + (long)triCount * 50L;
        if (expected == bytes.Length) return true;

        // If it starts with "solid" and doesn't match expected, assume ASCII.
        if (bytes.Length >= 5)
        {
            var s = Encoding.ASCII.GetString(bytes, 0, 5);
            if (string.Equals(s, "solid", StringComparison.OrdinalIgnoreCase)) return false;
        }

        // Fallback: if it doesn't look like ASCII and is reasonably large, assume binary.
        return true;
    }

    private static bool TryLoadBinary(byte[] bytes, bool swapYAndZ, out Mesh mesh, out string error)
    {
        mesh = null;
        error = null;

        if (bytes.Length < 84)
        {
            error = "Binary STL too small.";
            return false;
        }

        uint triCount = BitConverter.ToUInt32(bytes, 80);
        long expected = 84L + (long)triCount * 50L;
        if (expected > bytes.Length)
        {
            error = "Binary STL truncated.";
            return false;
        }
        if (expected < bytes.Length)
        {
            // Some exporters append extra bytes; be tolerant but clamp.
            triCount = (uint)Mathf.Max(0, (bytes.Length - 84) / 50);
        }

        var vertices = new Vector3[triCount * 3];
        var normals = new Vector3[triCount * 3];
        var indices = new int[triCount * 3];

        int offset = 84;
        for (int i = 0; i < triCount; i++)
        {
            float nx = BitConverter.ToSingle(bytes, offset + 0);
            float ny = BitConverter.ToSingle(bytes, offset + 4);
            float nz = BitConverter.ToSingle(bytes, offset + 8);
            var n = ConvertVec(new Vector3(nx, ny, nz), swapYAndZ);

            int baseV = i * 3;
            for (int v = 0; v < 3; v++)
            {
                float x = BitConverter.ToSingle(bytes, offset + 12 + v * 12 + 0);
                float y = BitConverter.ToSingle(bytes, offset + 12 + v * 12 + 4);
                float z = BitConverter.ToSingle(bytes, offset + 12 + v * 12 + 8);
                vertices[baseV + v] = ConvertVec(new Vector3(x, y, z), swapYAndZ);
                normals[baseV + v] = n;
                indices[baseV + v] = baseV + v;
            }

            offset += 50;
        }

        mesh = new Mesh();
        if (vertices.Length > 65535)
        {
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        }
        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.triangles = indices;
        mesh.RecalculateBounds();
        return true;
    }

    private static bool TryLoadAscii(byte[] bytes, bool swapYAndZ, out Mesh mesh, out string error)
    {
        mesh = null;
        error = null;

        string text;
        try
        {
            text = Encoding.ASCII.GetString(bytes);
        }
        catch (Exception ex)
        {
            error = $"ASCII decode failed: {ex.Message}";
            return false;
        }

        var verts = new List<Vector3>(4096);
        var norms = new List<Vector3>(4096);
        var indices = new List<int>(4096);

        Vector3 currentNormal = Vector3.up;
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("facet normal", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5 &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var nx) &&
                    float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var ny) &&
                    float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var nz))
                {
                    currentNormal = ConvertVec(new Vector3(nx, ny, nz), swapYAndZ);
                }
            }
            else if (line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                    float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                {
                    verts.Add(ConvertVec(new Vector3(x, y, z), swapYAndZ));
                    norms.Add(currentNormal);
                    indices.Add(verts.Count - 1);
                }
            }
        }

        if (verts.Count < 3)
        {
            error = "ASCII STL had no vertices.";
            return false;
        }

        mesh = new Mesh();
        if (verts.Count > 65535)
        {
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        }
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.SetTriangles(indices, 0);
        mesh.RecalculateBounds();
        return true;
    }

    private static Vector3 ConvertVec(Vector3 v, bool swapYAndZ)
    {
        // OpenSCAD is typically Z-up; Unity is Y-up.
        // swapYAndZ maps (x,y,z) -> (x,z,y).
        return swapYAndZ ? new Vector3(v.x, v.z, v.y) : v;
    }
}

