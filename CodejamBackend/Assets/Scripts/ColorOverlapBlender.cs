using System.Collections.Generic;
using Alchemy.Inspector;
using UnityEngine;

/// <summary>
/// Shows the weighted HSV blend wherever ColorBody sprites overlap.
/// Separate from the physics: it only reads each body's transform, sprite
/// and colour after physics has run, and feeds its overlapping neighbours
/// to the ColorOverlapBlend shader through a MaterialPropertyBlock.
/// Weights are areas, matching how ColorPhysics blends merged colours.
/// </summary>
public class ColorOverlapBlender : MonoBehaviour
{
    // Must match MAX_NEIGHBORS in ColorOverlapBlend.shader
    private const int MaxNeighbors = 4;

    [SerializeField, Required] private ColorPhysics physics;

    // Material using Codejam/ColorOverlapBlend; applied to every body
    [SerializeField, Required] private Material overlapMaterial;

    private static readonly int SelfColorId = Shader.PropertyToID("_SelfColor");
    private static readonly int SelfWeightId = Shader.PropertyToID("_SelfWeight");
    private static readonly int SelfFrameId = Shader.PropertyToID("_SelfFrame");
    private static readonly int SelfExtentsId = Shader.PropertyToID("_SelfExtents");
    private static readonly int NeighborCountId = Shader.PropertyToID("_NeighborCount");
    private static readonly int NeighborColorsId = Shader.PropertyToID("_NeighborColors");
    private static readonly int NeighborFramesId = Shader.PropertyToID("_NeighborFrames");
    private static readonly int NeighborExtentsId = Shader.PropertyToID("_NeighborExtents");

    /// <summary>A body's visible ellipse in world XY.</summary>
    private struct Shape
    {
        public SpriteRenderer Renderer;
        public Color Color;
        public Vector2 Center;
        public Vector2 AxisX;
        public Vector2 Extents;
        public float BoundRadius;
        public float Weight;
    }

    private readonly List<Shape> _shapes = new();
    private readonly List<(int index, float depth)> _neighbors = new();

    // Fixed-size so the shader array length never changes
    private readonly Vector4[] _colors = new Vector4[MaxNeighbors];
    private readonly Vector4[] _frames = new Vector4[MaxNeighbors];
    private readonly Vector4[] _extents = new Vector4[MaxNeighbors];

    private MaterialPropertyBlock _block;

    // LateUpdate so it reads positions/scales after physics and tweens
    private void LateUpdate()
    {
        if (physics == null || overlapMaterial == null)
            return;

        _block ??= new MaterialPropertyBlock();

        BuildShapes();

        for (int i = 0; i < _shapes.Count; i++)
        {
            FindNeighbors(i);
            ApplyToRenderer(i);
        }
    }

    private void BuildShapes()
    {
        _shapes.Clear();

        foreach (ColorBody body in physics.Bodies)
        {
            if (body == null)
                continue;

            SpriteRenderer sr = body.Renderer;

            if (sr == null || sr.sprite == null)
                continue;

            if (sr.sharedMaterial != overlapMaterial)
                sr.sharedMaterial = overlapMaterial;

            Transform t = body.transform;
            Bounds spriteBounds = sr.sprite.bounds;
            Vector3 scale = t.lossyScale;

            Vector2 extents = new(
                Mathf.Abs(spriteBounds.extents.x * scale.x),
                Mathf.Abs(spriteBounds.extents.y * scale.y)
            );

            // Physics radius, so overlap weights match merge weights
            float radius = body.Radius;

            _shapes.Add(new Shape
            {
                Renderer = sr,
                Color = sr.color,
                Center = t.TransformPoint(spriteBounds.center),
                AxisX = ((Vector2)t.right).normalized,
                Extents = extents,
                BoundRadius = Mathf.Max(extents.x, extents.y),
                Weight = radius * radius
            });
        }
    }

    /// <summary>
    /// Collects shapes whose bounding circles overlap shape i, deepest
    /// overlap first, capped at MaxNeighbors.
    /// </summary>
    private void FindNeighbors(int i)
    {
        _neighbors.Clear();

        Shape self = _shapes[i];

        for (int j = 0; j < _shapes.Count; j++)
        {
            if (j == i)
                continue;

            Shape other = _shapes[j];

            float depth =
                self.BoundRadius + other.BoundRadius -
                Vector2.Distance(self.Center, other.Center);

            if (depth > 0f)
                _neighbors.Add((j, depth));
        }

        if (_neighbors.Count > MaxNeighbors)
            _neighbors.Sort((a, b) => b.depth.CompareTo(a.depth));
    }

    private void ApplyToRenderer(int i)
    {
        Shape self = _shapes[i];
        int count = Mathf.Min(_neighbors.Count, MaxNeighbors);

        for (int n = 0; n < MaxNeighbors; n++)
        {
            if (n >= count)
            {
                _colors[n] = Vector4.zero;
                _frames[n] = Vector4.zero;
                _extents[n] = Vector4.one;
                continue;
            }

            Shape other = _shapes[_neighbors[n].index];

            // Raw sRGB values; the shader linearises after blending
            _colors[n] = other.Color;
            _frames[n] = FrameOf(other);
            _extents[n] = ExtentsOf(other);
        }

        self.Renderer.GetPropertyBlock(_block);

        _block.SetVector(SelfColorId, self.Color);
        _block.SetFloat(SelfWeightId, self.Weight);
        _block.SetVector(SelfFrameId, FrameOf(self));
        _block.SetVector(SelfExtentsId, ExtentsOf(self));
        _block.SetFloat(NeighborCountId, count);
        _block.SetVectorArray(NeighborColorsId, _colors);
        _block.SetVectorArray(NeighborFramesId, _frames);
        _block.SetVectorArray(NeighborExtentsId, _extents);

        self.Renderer.SetPropertyBlock(_block);
    }

    // xy = centre, zw = local X axis (world, unit)
    private static Vector4 FrameOf(Shape shape) => new(
        shape.Center.x, shape.Center.y,
        shape.AxisX.x, shape.AxisX.y
    );

    // xy = half size along local X/Y, z = area weight
    private static Vector4 ExtentsOf(Shape shape) => new(
        Mathf.Max(shape.Extents.x, 0.0001f),
        Mathf.Max(shape.Extents.y, 0.0001f),
        shape.Weight,
        0f
    );
}
