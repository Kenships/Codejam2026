using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// UI Toolkit port of the frontend's PixelBlast background (square variant).
/// Drifting fbm noise is sampled per 8x8-pixel cell, smoothed between cells
/// and Bayer-dithered into a grid of squares. The pointer leaves a fading
/// "reveal" trail, clicks send out ripples, and it opens with an intro ring.
/// Drawn as plain quads in generateVisualContent, so no custom shader.
/// </summary>
[UxmlElement]
public partial class PixelBlastBackground : VisualElement
{
    private const int MaxClicks = 10;
    private const float IntroSeconds = 1.8f;
    private const int MaxQuadsPerChunk = 16000;      // 64k vertices per Allocate

    [UxmlAttribute] public float pixelSize = 10f;
    [UxmlAttribute] public float patternScale = 2f;
    [UxmlAttribute] public float patternDensity = 4f;
    [UxmlAttribute] public float speed = 0.5f;
    [UxmlAttribute] public float drift = 0.1f;
    [UxmlAttribute] public float driftAngle = 30f;
    [UxmlAttribute] public float softness = 0.6f;
    [UxmlAttribute] public float glow = 0.5f;
    [UxmlAttribute] public float twinkle = 0.25f;
    [UxmlAttribute] public float edgeFade = 0.5f;
    [UxmlAttribute] public bool revealOnHover = true;
    [UxmlAttribute] public float hoverRadius = 50f;
    [UxmlAttribute] public bool enableRipples = true;
    [UxmlAttribute] public float rippleSpeed = 0.3f;
    [UxmlAttribute] public float rippleThickness = 0.4f;
    [UxmlAttribute] public float rippleIntensity = 1f;
    [UxmlAttribute] public bool intro = true;
    [UxmlAttribute] public Color color = Color.black;
    [UxmlAttribute] public Color accentColor = Color.white;

    private static readonly float[] Bayer = BuildBayer();

    // Cell grid (b channel of the original cell texture + hover trail)
    private float[] _cells = new float[0];
    private float[] _trail = new float[0];
    private int _cellMinX, _cellMinY, _cellCols, _cellRows;
    private float _cellSize;

    // Animation state
    private IVisualElementScheduledItem _tick;
    private float _lastTime = -1f;
    private float _elapsed, _clock, _introClock;
    private float _driftX, _driftY;
    private Vector2 _mouse, _pointer;
    private bool _pointerInside;
    private float _presence;

    private readonly Vector2[] _clickPos = new Vector2[MaxClicks];
    private readonly float[] _clickAt = new float[MaxClicks];
    private readonly bool[] _clickUsed = new bool[MaxClicks];
    private int _clickIndex;

    private VisualElement _panelRoot;

    public PixelBlastBackground()
    {
        pickingMode = PickingMode.Ignore;
        generateVisualContent += Draw;

        RegisterCallback<AttachToPanelEvent>(OnAttach);
        RegisterCallback<DetachFromPanelEvent>(OnDetach);
    }

    #region Lifecycle

    private void OnAttach(AttachToPanelEvent evt)
    {
        _introClock = intro ? 0f : 1f;
        _lastTime = -1f;

        // Listen on the whole panel (like the frontend's window listeners),
        // since the menu sits on top of this element
        _panelRoot = evt.destinationPanel.visualTree;
        _panelRoot.RegisterCallback<PointerMoveEvent>(OnPointerMove, TrickleDown.TrickleDown);
        _panelRoot.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
        _panelRoot.RegisterCallback<PointerLeaveEvent>(OnPointerLeave);

        _tick = schedule.Execute(Tick).Every(16);
    }

    private void OnDetach(DetachFromPanelEvent evt)
    {
        _tick?.Pause();
        _tick = null;

        if (_panelRoot == null)
            return;

        _panelRoot.UnregisterCallback<PointerMoveEvent>(OnPointerMove, TrickleDown.TrickleDown);
        _panelRoot.UnregisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
        _panelRoot.UnregisterCallback<PointerLeaveEvent>(OnPointerLeave);
        _panelRoot = null;
    }

    #endregion

    #region Input

    private void OnPointerMove(PointerMoveEvent evt)
    {
        _pointer = this.WorldToLocal(evt.position);

        if (!_pointerInside)
            _mouse = _pointer;

        _pointerInside = true;
    }

    private void OnPointerDown(PointerDownEvent evt)
    {
        _pointer = this.WorldToLocal(evt.position);
        _pointerInside = true;

        if (enableRipples)
            AddRipple(_pointer);
    }

    private void OnPointerLeave(PointerLeaveEvent evt) => _pointerInside = false;

    public void AddRipple(Vector2 localPosition)
    {
        _clickPos[_clickIndex] = localPosition;
        _clickAt[_clickIndex] = _clock;
        _clickUsed[_clickIndex] = true;
        _clickIndex = (_clickIndex + 1) % MaxClicks;
    }

    #endregion

    #region Simulation

    private void Tick()
    {
        float now = Time.realtimeSinceStartup;
        float dt = _lastTime < 0f ? 0f : Mathf.Clamp(now - _lastTime, 0f, 1f / 20f);
        _lastTime = now;

        _elapsed += dt;
        _clock += dt;

        float angle = driftAngle * Mathf.Deg2Rad;
        _driftX += Mathf.Cos(angle) * drift * dt * 0.05f;
        _driftY += Mathf.Sin(angle) * drift * dt * 0.05f;

        float follow = 1f - Mathf.Exp(-dt / 0.08f);
        if (_pointerInside)
            _mouse += (_pointer - _mouse) * follow;

        _presence += ((_pointerInside ? 1f : 0f) - _presence) *
                     (1f - Mathf.Exp(-dt / 0.25f));

        if (_introClock < 1f)
            _introClock = Mathf.Min(1f, _introClock + dt / IntroSeconds);

        float decay = revealOnHover ? Mathf.Exp(-dt / 0.45f) : 0f;

        if (LayoutCells())
            StepCells(decay);

        MarkDirtyRepaint();
    }

    /// <summary>
    /// Sizes the cell grid to cover the element (plus a margin cell on each
    /// side for smoothing). Returns false while the element has no size.
    /// </summary>
    private bool LayoutCells()
    {
        Rect rect = contentRect;

        if (rect.width < 1f || rect.height < 1f || pixelSize <= 0f)
            return false;

        _cellSize = 8f * pixelSize;

        float halfW = rect.width * 0.5f;
        float halfH = rect.height * 0.5f;

        int minX = Mathf.FloorToInt(-halfW / _cellSize) - 1;
        int minY = Mathf.FloorToInt(-halfH / _cellSize) - 1;
        int cols = Mathf.FloorToInt(halfW / _cellSize) + 2 - minX;
        int rows = Mathf.FloorToInt(halfH / _cellSize) + 2 - minY;

        if (cols != _cellCols || rows != _cellRows ||
            minX != _cellMinX || minY != _cellMinY)
        {
            _cellMinX = minX;
            _cellMinY = minY;
            _cellCols = cols;
            _cellRows = rows;
            _cells = new float[cols * rows];
            _trail = new float[cols * rows];
        }

        return true;
    }

    private void StepCells(float decay)
    {
        Rect rect = contentRect;
        Vector2 center = rect.center;
        float resY = rect.height;
        float aspect = rect.width / rect.height;

        float time = _elapsed * speed;
        float radius = Mathf.Max(hoverRadius, 1f);
        float introEased = 1f - Mathf.Pow(1f - _introClock, 2.4f);
        float introReach = new Vector2(aspect, 1f).magnitude * 0.5f + 0.2f;
        float introFront = introEased * introReach;
        Vector2 introCenter = new Vector2(-_cellSize * 0.5f, -_cellSize * 0.5f) / resY;

        for (int y = 0; y < _cellRows; y++)
        {
            for (int x = 0; x < _cellCols; x++)
            {
                int index = y * _cellCols + x;

                // Cell corner in centred coordinates
                Vector2 cellCoord = new Vector2(x + _cellMinX, y + _cellMinY) * _cellSize;
                Vector2 uv = cellCoord / resY;

                float baseNoise = Fbm(uv.x + _driftX, uv.y + _driftY, time * 0.05f);
                float feed = baseNoise * 0.5f - 0.65f + (patternDensity - 0.5f) * 0.3f;

                if (enableRipples)
                    feed = Mathf.Max(feed, RippleFeed(uv, center, resY));

                if (_introClock < 1f)
                {
                    float r = Vector2.Distance(uv, introCenter);
                    float shown = SmoothStep(introFront + 0.04f, introFront - 0.06f, r);
                    float d = (r - introFront) / 0.07f;
                    float ring = Mathf.Exp(-d * d) * (1f - _introClock);

                    feed = Mathf.Lerp(-1f, feed, shown);
                    feed = Mathf.Max(feed, ring * 1.4f);
                }

                // Pointer brush leaves a decaying trail that pushes the
                // pattern up ("reveal")
                float brush = 0f;

                if (revealOnHover)
                {
                    Vector2 delta = (cellCoord + Vector2.one * (_cellSize * 0.5f) + center - _mouse) / radius;
                    brush = Mathf.Exp(-Vector2.Dot(delta, delta)) * _presence;
                }

                float trail = Mathf.Max(brush, Mathf.Max(_trail[index] * decay - 1f / 255f, 0f));
                _trail[index] = trail;

                if (revealOnHover)
                    feed += trail * 0.9f;

                _cells[index] = Mathf.Clamp01((feed + 0.5f) * 0.5f);
            }
        }
    }

    private float RippleFeed(Vector2 uv, Vector2 center, float resY)
    {
        float result = float.NegativeInfinity;
        float thickness = Mathf.Max(rippleThickness, 0.0001f);

        for (int i = 0; i < MaxClicks; i++)
        {
            if (!_clickUsed[i])
                continue;

            float t = Mathf.Max((_clock - _clickAt[i]) * 0.5f, 0f);

            if (t > 8f)
                continue;

            Vector2 cuv = (_clickPos[i] - center - Vector2.one * (_cellSize * 0.5f)) / resY;
            float r = Vector2.Distance(uv, cuv);
            float d = (r - rippleSpeed * t) / thickness;
            float ring = Mathf.Exp(-d * d);
            float atten = Mathf.Exp(-t) * Mathf.Exp(-10f * r);

            result = Mathf.Max(result, ring * atten * rippleIntensity);
        }

        return result;
    }

    /// <summary>Bilinear cell value at a point (centred coordinates).</summary>
    private float SampleCells(float fx, float fy)
    {
        float gx = fx / _cellSize - _cellMinX - 0.5f;
        float gy = fy / _cellSize - _cellMinY - 0.5f;

        int x0 = Mathf.FloorToInt(gx);
        int y0 = Mathf.FloorToInt(gy);
        float tx = gx - x0;
        float ty = gy - y0;

        float a = Cell(x0, y0);
        float b = Cell(x0 + 1, y0);
        float c = Cell(x0, y0 + 1);
        float d = Cell(x0 + 1, y0 + 1);

        return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
    }

    private float Cell(int x, int y)
    {
        x = Mathf.Clamp(x, 0, _cellCols - 1);
        y = Mathf.Clamp(y, 0, _cellRows - 1);
        return _cells[y * _cellCols + x];
    }

    #endregion

    #region Drawing

    private void Draw(MeshGenerationContext context)
    {
        if (_cells.Length == 0)
            return;

        Rect rect = contentRect;

        if (rect.width < 1f || rect.height < 1f)
            return;

        Vector2 center = rect.center;
        float halfW = rect.width * 0.5f;
        float halfH = rect.height * 0.5f;
        float soft = 0.12f * softness;
        float heatScale = Mathf.Min(glow * 2f, 1f);

        int minPx = Mathf.FloorToInt(-halfW / pixelSize);
        int maxPx = Mathf.CeilToInt(halfW / pixelSize) - 1;
        int minPy = Mathf.FloorToInt(-halfH / pixelSize);
        int maxPy = Mathf.CeilToInt(halfH / pixelSize) - 1;

        QuadBatch batch = new(context);

        for (int py = minPy; py <= maxPy; py++)
        {
            for (int px = minPx; px <= maxPx; px++)
            {
                float fx = (px + 0.5f) * pixelSize;
                float fy = (py + 0.5f) * pixelSize;

                float feed = SampleCells(fx, fy) * 2f - 0.5f;

                if (feed < -soft && feed < 0.2f)
                    continue;

                // Dithered on/off, then square size from coverage
                float bayer = Bayer[Mod8(py) * 8 + Mod8(px)] - 0.5f;
                float margin = feed + bayer - 0.5f;
                float bw = soft > 0f ? SmoothStep(-soft, soft, margin) : (margin >= 0f ? 1f : 0f);

                float heat = Mathf.Clamp01((feed - 1f) * 2.5f) * heatScale;
                float coverage = Mathf.Clamp01(bw * (1f + heat * 0.35f));

                float mask = coverage > 0f ? 1f : 0f;

                if (twinkle > 0f)
                {
                    float h = Hash(px, py);
                    float rate = 1.5f + 3f * Fract(h * 7.31f);
                    float wave = 0.5f + 0.5f * Mathf.Sin(_clock * rate + h * 37f);
                    mask *= 1f - twinkle * 0.55f * wave * wave;
                }

                float density = Mathf.Clamp01(feed);
                float glowAlpha = 0f;

                if (glow > 0f)
                {
                    float bloom = SmoothStep(0.2f, 1f, density);
                    glowAlpha = bloom * bloom * glow * 0.22f;
                }

                float fade = EdgeFade(fx + halfW, fy + halfH, rect.width, rect.height);
                mask *= fade;
                glowAlpha *= fade;

                float tone = SmoothStep(0.3f, 1f, density) * 0.85f;
                Color tint = Color.Lerp(color, accentColor, Mathf.Max(tone, heat));
                Color lit = Color.Lerp(tint, Color.white, 0.45f * heat);

                float left = center.x + px * pixelSize;
                float top = center.y + py * pixelSize;

                // Soft haze behind the square (the original's glow term)
                if (glowAlpha > 1f / 255f)
                    batch.Add(left, top, pixelSize, WithAlpha(tint, glowAlpha));

                if (mask > 1f / 255f && coverage > 0f)
                {
                    float side = Mathf.Sqrt(coverage) * pixelSize;
                    float inset = (pixelSize - side) * 0.5f;
                    batch.Add(left + inset, top + inset, side, WithAlpha(lit, mask));
                }
            }
        }

        batch.Flush();
    }

    private float EdgeFade(float x, float y, float width, float height)
    {
        if (edgeFade <= 0f)
            return 1f;

        float nx = x / width;
        float ny = y / height;

        return Smoother(Mathf.Clamp01(nx / edgeFade)) *
               Smoother(Mathf.Clamp01(ny / edgeFade)) *
               Smoother(Mathf.Clamp01((1f - nx) / edgeFade)) *
               Smoother(Mathf.Clamp01((1f - ny) / edgeFade));
    }

    private static Color32 WithAlpha(Color c, float alpha) =>
        new Color(c.r, c.g, c.b, Mathf.Clamp01(alpha));

    /// <summary>Collects squares and writes them in 64k-vertex chunks.</summary>
    private struct QuadBatch
    {
        private static Vertex[] _vertices = new Vertex[MaxQuadsPerChunk * 4];

        private readonly MeshGenerationContext _context;
        private int _quads;

        public QuadBatch(MeshGenerationContext context)
        {
            _context = context;
            _quads = 0;
        }

        public void Add(float x, float y, float size, Color32 tint)
        {
            if (_quads == MaxQuadsPerChunk)
                Flush();

            int v = _quads * 4;
            float z = Vertex.nearZ;

            _vertices[v + 0] = new Vertex { position = new Vector3(x, y, z), tint = tint };
            _vertices[v + 1] = new Vertex { position = new Vector3(x + size, y, z), tint = tint };
            _vertices[v + 2] = new Vertex { position = new Vector3(x + size, y + size, z), tint = tint };
            _vertices[v + 3] = new Vertex { position = new Vector3(x, y + size, z), tint = tint };

            _quads++;
        }

        public void Flush()
        {
            if (_quads == 0)
                return;

            MeshWriteData mesh = _context.Allocate(_quads * 4, _quads * 6);

            for (int i = 0; i < _quads * 4; i++)
                mesh.SetNextVertex(_vertices[i]);

            for (int q = 0; q < _quads; q++)
            {
                ushort v = (ushort)(q * 4);
                mesh.SetNextIndex(v);
                mesh.SetNextIndex((ushort)(v + 1));
                mesh.SetNextIndex((ushort)(v + 2));
                mesh.SetNextIndex((ushort)(v + 2));
                mesh.SetNextIndex((ushort)(v + 3));
                mesh.SetNextIndex(v);
            }

            _quads = 0;
        }
    }

    #endregion

    #region Noise

    private float Fbm(float x, float y, float t)
    {
        float px = x * patternScale;
        float py = y * patternScale;
        float sum = 1f;
        float freq = 1f;

        for (int i = 0; i < 5; i++)
        {
            sum += ValueNoise(px * freq, py * freq, t * freq);
            freq *= 1.25f;
        }

        return sum * 0.5f + 0.5f;
    }

    private static float ValueNoise(float x, float y, float z)
    {
        float ix = Mathf.Floor(x), iy = Mathf.Floor(y), iz = Mathf.Floor(z);
        float fx = x - ix, fy = y - iy, fz = z - iz;

        float n000 = Hash3(ix, iy, iz);
        float n100 = Hash3(ix + 1f, iy, iz);
        float n010 = Hash3(ix, iy + 1f, iz);
        float n110 = Hash3(ix + 1f, iy + 1f, iz);
        float n001 = Hash3(ix, iy, iz + 1f);
        float n101 = Hash3(ix + 1f, iy, iz + 1f);
        float n011 = Hash3(ix, iy + 1f, iz + 1f);
        float n111 = Hash3(ix + 1f, iy + 1f, iz + 1f);

        float wx = Smoother(fx), wy = Smoother(fy), wz = Smoother(fz);

        float x00 = Mathf.Lerp(n000, n100, wx);
        float x10 = Mathf.Lerp(n010, n110, wx);
        float x01 = Mathf.Lerp(n001, n101, wx);
        float x11 = Mathf.Lerp(n011, n111, wx);

        return Mathf.Lerp(Mathf.Lerp(x00, x10, wy), Mathf.Lerp(x01, x11, wy), wz) * 2f - 1f;
    }

    private static float Hash3(float x, float y, float z) =>
        Fract(Mathf.Sin(x + y * 57f + z * 113f) * 43758.5453f);

    private static float Hash(int x, int y) =>
        Fract((float)(System.Math.Sin(x * 127.1 + y * 311.7) * 43758.5453));

    private static float[] BuildBayer()
    {
        static float Bayer2(float x, float y)
        {
            float fx = Mathf.Floor(x), fy = Mathf.Floor(y);
            return (fx / 2f + fy * fy * 0.75f) % 1f;
        }

        float[] table = new float[64];

        for (int i = 0; i < 64; i++)
        {
            float x = i % 8;
            float y = i / 8;
            table[i] = (Bayer2(x / 4f, y / 4f) * 0.25f + Bayer2(x / 2f, y / 2f)) * 0.25f + Bayer2(x, y);
        }

        return table;
    }

    #endregion

    #region Math

    private static int Mod8(int v) => ((v % 8) + 8) % 8;

    private static float Fract(float v) => v - Mathf.Floor(v);

    // GLSL smoothstep (edges may be reversed)
    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }

    private static float Smoother(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    #endregion
}
