using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using OpenTK;

namespace PSXPrev.Common.Exporters
{
    public class OBJExporter
    {
        private StreamWriter _writer;
        private PNGExporter _pngExporter;
        private MTLExporter _mtlExporter;
        private MTLExporter.MaterialDictionary _mtlDictionary;
        private Dictionary<Tuple<Vector3, Color3>, int> _positionIndices; // position, color
        private Dictionary<Vector3, int> _normalIndices;
        private Dictionary<Vector2, int> _uvIndices;
        private ModelPreparerExporter _modelPreparer;
        private ExportModelOptions _options;
        private string _baseName;

        public int Export(ExportModelOptions options, RootEntity[] entities)
        {
            _options = options?.Clone() ?? new ExportModelOptions();
            // Force any required options for this format here, before calling Validate.
            _options.Validate("obj");

            _pngExporter = new PNGExporter();
            _mtlDictionary = new MTLExporter.MaterialDictionary();
            _modelPreparer = new ModelPreparerExporter(_options);

            // Prepare the shared state for all models being exported (mainly setting up tiled textures).
            var groups = _modelPreparer.PrepareAll(entities);

            for (var i = 0; i < groups.Length; i++)
            {
                var group = groups[i];
                // Prepare the state for the current model being exported.
                var preparedEntities = _modelPreparer.PrepareCurrent(entities, group, out var preparedModels);

                ExportEntities(i, group, preparedEntities, preparedModels);
            }

            //_pngExporter.Dispose();
            _pngExporter = null;
            _mtlDictionary = null;
            _modelPreparer.Dispose();
            _modelPreparer = null;

            return groups.Length;
        }

        private void ExportEntities(int index, Tuple<int, long> group, RootEntity[] entities, List<ModelEntity> models)
        {
            // If shared, reuse the dictionary of textures so that we only export them once.
            // We're using a separate mtl file for each model so that unused materials aren't added.
            if (!_options.ShareTextures)
            {
                _mtlDictionary.Clear();
            }

            _baseName = _options.GetBaseName(index);
            _mtlExporter = new MTLExporter(_options, _baseName, _mtlDictionary);
            _writer = new StreamWriter(Path.Combine(_options.Path, $"{_baseName}.obj"));

            _positionIndices = new Dictionary<Tuple<Vector3, Color3>, int>();
            _normalIndices = new Dictionary<Vector3, int>();
            _uvIndices = new Dictionary<Vector2, int>();

            // Write mtl file reference
            _writer.WriteLine("mtllib {0}", _mtlExporter.FileName);

            // Write vertices and export materials
            foreach (var model in models)
            {
                WriteModel(model);
            }

            // Write objects/groups and their faces
            var vertexIndex = 1; // Index for vertex positions and normals (OBJ format is 1-indexed)
            var uvIndex     = 1; // Index for UVs
            for (var i = 0; i < entities.Length; i++)
            {
                // Note that models in entities are guaranteed to appear in the same order as in models
                var entity = entities[i];

                // Write start of object
                _writer.WriteLine("o object{0}", i);
                for (var j = 0; j < entity.ChildEntities.Length; j++)
                {
                    var model = (ModelEntity)entity.ChildEntities[j];
                    WriteGroup(i, j, ref vertexIndex, ref uvIndex, model);
                }
            }

            _positionIndices = null;
            _normalIndices = null;
            _uvIndices = null;
            _mtlExporter.Dispose();
            _mtlExporter = null;
            _writer.Dispose();
            _writer = null;
        }

        // ============================================================
        // MODIFIED: WriteModel now interpolates VDF vertex/normal data
        // ============================================================
        private void WriteModel(ModelEntity model)
        {
            // Export material if we haven't already
            var texture = model.Texture;
            if (NeedsTexture(model) && _mtlExporter.AddMaterial(texture, out var materialId))
            {
                var textureName = _options.GetTextureName(_baseName, materialId);
                _pngExporter.Export(texture, textureName, _options.Path);
            }

            var worldMatrix = model.WorldMatrix;
            GeomMath.InvertSafe(ref worldMatrix, out var invWorldMatrix);

            // Build a lookup from original vertex position → index.
            // This is used to map triangle corners to the correct element in the animation arrays.
            Dictionary<Vector3, int> vertexIndexLookup = null;
            bool hasVertexAnim = model.InitialVertices != null && model.FinalVertices != null;
            bool hasNormalAnim = model.InitialNormals != null && model.FinalNormals != null;

            if (hasVertexAnim || hasNormalAnim)
            {
                vertexIndexLookup = new Dictionary<Vector3, int>(new Vector3Comparer());
                for (int i = 0; i < model.Vertices.Length; i++)
                {
                    vertexIndexLookup[model.Vertices[i]] = i;
                }
            }

            // Write vertex positions (interpolated if VDF animation is active)
            foreach (var triangle in model.Triangles)
            {
                for (var j = 2; j >= 0; j--)
                {
                    Vector3 localVertex = triangle.Vertices[j];
                    if (hasVertexAnim)
                    {
                        if (vertexIndexLookup.TryGetValue(localVertex, out int idx))
                        {
                            float t = model.Interpolator;
                            localVertex = Vector3.Lerp(model.InitialVertices[idx], model.FinalVertices[idx], t);
                        }
                        // If lookup fails, fall back to the original vertex (should not happen in practice)
                    }
                    WriteVertexPosition(localVertex, triangle.Colors[j], ref worldMatrix);
                }
            }

            // Write vertex normals (interpolated if NormalDiff animation is active)
            foreach (var triangle in model.Triangles)
            {
                for (var j = 2; j >= 0; j--)
                {
                    Vector3 localNormal = triangle.Normals[j];
                    if (hasNormalAnim)
                    {
                        // Use the corresponding vertex to find the index in the normal arrays.
                        Vector3 correspondingVertex = triangle.Vertices[j];
                        if (vertexIndexLookup.TryGetValue(correspondingVertex, out int idx))
                        {
                            float t = model.Interpolator;
                            localNormal = Vector3.Lerp(model.InitialNormals[idx], model.FinalNormals[idx], t);
                            localNormal.Normalize();
                        }
                    }
                    WriteNormal(localNormal, ref invWorldMatrix);
                }
            }

            // Write vertex UVs (unchanged)
            if (NeedsTexture(model))
            {
                foreach (var triangle in model.Triangles)
                {
                    for (var j = 2; j >= 0; j--)
                    {
                        WriteUV(triangle.Uv[j]);
                    }
                }
            }
        }

        private void WriteVertexPosition(Vector3 localVertex, Color3 color, ref Matrix4 worldMatrix)
        {
            Vector3.TransformPosition(ref localVertex, ref worldMatrix, out var vertex);
            if (_options.VertexIndexReuse)
            {
                var colorVec = _options.ExperimentalOBJVertexColor ? color : Color3.Black;
                var tuple = new Tuple<Vector3, Color3>(vertex, colorVec);
                if (_positionIndices.ContainsKey(tuple))
                {
                    return; // Vertex position/color already defined
                }
                _positionIndices.Add(tuple, _positionIndices.Count + 1); // +1 because indices are 1-indexed
            }
            var vertexColor = string.Empty;
            if (_options.ExperimentalOBJVertexColor)
            {
                vertexColor = string.Format(" {0} {1} {2}", F(color.R), F(color.G), F(color.B));
            }
            _writer.WriteLine("v {0} {1} {2}{3}", F(vertex.X), F(-vertex.Y), F(-vertex.Z), vertexColor);
        }

        private void WriteNormal(Vector3 localNormal, ref Matrix4 invWorldMatrix)
        {
            GeomMath.TransformNormalInverseNormalized(ref localNormal, ref invWorldMatrix, out var normal);
            if (_options.VertexIndexReuse)
            {
                if (_normalIndices.ContainsKey(normal))
                {
                    return; // Vertex normal already defined
                }
                _normalIndices.Add(normal, _normalIndices.Count + 1); // +1 because indices are 1-indexed
            }
            _writer.WriteLine("vn {0} {1} {2}", F(normal.X), F(-normal.Y), F(-normal.Z));
        }

        private void WriteUV(Vector2 uv)
        {
            if (_options.VertexIndexReuse)
            {
                if (_uvIndices.ContainsKey(uv))
                {
                    return; // Vertex UV already defined
                }
                _uvIndices.Add(uv, _uvIndices.Count + 1); // +1 because indices are 1-indexed
            }
            _writer.WriteLine("vt {0} {1}", F(uv.X), F(1f - uv.Y));
        }

        private void WriteGroup(int objectIndex, int groupIndex, ref int vertexIndex, ref int uvIndex, ModelEntity model)
        {
            var worldMatrix = model.WorldMatrix;
            GeomMath.InvertSafe(ref worldMatrix, out var invWorldMatrix);
            var needsTexture = NeedsTexture(model);
            var materialName = _mtlExporter.GetMaterialName(_options.ExportTextures ? model.Texture : null);

            // Write start of group
            _writer.WriteLine("g group{0}_{1}", objectIndex, groupIndex);
            _writer.WriteLine("usemtl {0}", materialName);
            // Write group faces
            foreach (var triangle in model.Triangles)
            {
                if (_options.VertexIndexReuse)
                {
                    _writer.Write("f");
                    for (var j = 2; j >= 0; j--)
                    {
                        // We're using ref parameters as local variables here, the ref aspect isn't important for VertexIndexReuse.
                        vertexIndex = GetVertexPosition(triangle.Vertices[j], triangle.Colors[j], ref worldMatrix);
                        var normalIndex = GetNormal(triangle.Normals[j], ref invWorldMatrix);
                        if (needsTexture)
                        {
                            uvIndex = GetUV(triangle.Uv[j]);
                            _writer.Write(" {0}/{1}/{2}", vertexIndex, uvIndex, normalIndex);
                        }
                        else
                        {
                            _writer.Write(" {0}//{1}", vertexIndex, normalIndex);
                        }
                    }
                    _writer.WriteLine();
                }
                else
                {
                    if (needsTexture)
                    {
                        // v/vt/vn
                        _writer.WriteLine("f {0}/{3}/{0} {1}/{4}/{1} {2}/{5}/{2}",
                                          vertexIndex++, vertexIndex++, vertexIndex++, uvIndex++, uvIndex++, uvIndex++);
                    }
                    else
                    {
                        // v//vn
                        _writer.WriteLine("f {0}//{0} {1}//{1} {2}//{2}", vertexIndex++, vertexIndex++, vertexIndex++);
                    }
                }
            }
        }

        private int GetVertexPosition(Vector3 localVertex, Color3 color, ref Matrix4 worldMatrix)
        {
            Vector3.TransformPosition(ref localVertex, ref worldMatrix, out var vertex);
            var colorVec = _options.ExperimentalOBJVertexColor ? color : Color3.Black;
            return _positionIndices[new Tuple<Vector3, Color3>(vertex, colorVec)];
        }

        private int GetNormal(Vector3 localNormal, ref Matrix4 invWorldMatrix)
        {
            GeomMath.TransformNormalInverseNormalized(ref localNormal, ref invWorldMatrix, out var normal);
            return _normalIndices[normal];
        }

        private int GetUV(Vector2 uv)
        {
            return _uvIndices[uv];
        }

        private bool NeedsTexture(ModelEntity model)
        {
            return _options.ExportTextures && model.HasTexture;
        }

        private string F(float value)
        {
            return value.ToString(_options.FloatFormat, NumberFormatInfo.InvariantInfo);
        }

        private static string I(float value)
        {
            return value.ToString(GeomMath.IntegerFormat, NumberFormatInfo.InvariantInfo);
        }

        // ============================================================
        // Tolerance comparer for Vector3 (used for vertex index lookup)
        // ============================================================
        private sealed class Vector3Comparer : IEqualityComparer<Vector3>
        {
            private const float Epsilon = 1e-6f;

            public bool Equals(Vector3 a, Vector3 b)
            {
                return Math.Abs(a.X - b.X) < Epsilon &&
                       Math.Abs(a.Y - b.Y) < Epsilon &&
                       Math.Abs(a.Z - b.Z) < Epsilon;
            }

            public int GetHashCode(Vector3 v)
            {
                // Round to the nearest multiple of epsilon to get a stable hash.
                int hx = (int)Math.Round(v.X / Epsilon);
                int hy = (int)Math.Round(v.Y / Epsilon);
                int hz = (int)Math.Round(v.Z / Epsilon);
                return hx ^ (hy << 8) ^ (hz << 16);
            }
        }
    }
}
