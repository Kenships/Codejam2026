using Core.Utility;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

/// <summary>
/// Persistent full-screen PixelBlastBackground that lives across all scenes and pages
/// (Menu, Lobby, Drawing, Guessing, Reveal, Finished) without resetting its drift or
/// ripple simulation.
///
/// Screen-space UI always draws over the camera, so a UI background would sit on top of
/// the world-space colour cubes and wash out their colours. Instead this renders into its
/// own render texture, which is shown on a quad at the very back of the main camera's
/// view (lowest sorting order). Cubes and other sprites draw in front of it, and the rest
/// of the UI still draws over everything. The pointer trail and click ripples are fed
/// from the Input System, since a render-texture panel gets no pointer events.
/// </summary>
public class PersistentPixelBlastBackground : Singleton<PersistentPixelBlastBackground>
{
    private const float BackgroundOpacity = 0.6f;
    private const int BackgroundPanelSortingOrder = -100;
    private const string CompositeShaderResource = "Shaders/UIBackgroundComposite";
    private const string CompositeShaderName = "Hidden/TrueHue/UIBackgroundComposite";

    private UIDocument _document;
    private PanelSettings _panelSettings;
    private PixelBlastBackground _blast;
    private Color _accentColor = Color.white;

    // Render-texture path (null material = fall back to a screen-space overlay)
    private RenderTexture _texture;
    private Material _material;
    private Mesh _quadMesh;
    private MeshRenderer _quadRenderer;

    public Color AccentColor => _accentColor;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void BootstrapOnLoad()
    {
        EnsureExists();
    }

    public static PersistentPixelBlastBackground EnsureExists()
    {
        if (Instance != null)
            return Instance;

        PersistentPixelBlastBackground existing = FindFirstObjectByType<PersistentPixelBlastBackground>();
        if (existing != null)
            return existing;

        GameObject go = new("PersistentPixelBlastBackground");
        return go.AddComponent<PersistentPixelBlastBackground>();
    }

    protected override void Awake()
    {
        base.Awake();

        if (Instance != this)
            return;

        _document = GetComponent<UIDocument>();
        if (_document == null)
            _document = gameObject.AddComponent<UIDocument>();
        _document.sortingOrder = BackgroundPanelSortingOrder;

        CreateBackdropQuad();
        EnsurePanelSettingsAssigned();
        EnsureBlastElement();

        SceneManager.sceneLoaded += OnSceneLoaded;
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    protected override void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;

        if (Instance == this)
        {
            DestroyOwned(_material);
            DestroyOwned(_quadMesh);
            DestroyOwned(_panelSettings);

            if (_texture != null)
            {
                _texture.Release();
                DestroyOwned(_texture);
            }
        }

        base.OnDestroy();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        EnsurePanelSettingsAssigned();
        EnsureBlastElement();
    }

    private void Update()
    {
        EnsurePanelSettingsAssigned();
        EnsureTextureSize();
        EnsureBlastElement();
        FeedPointer();

        if (_blast != null)
            _blast.ForceTick();

        // Shown again (and re-fitted) by OnBeginCameraRendering once there's a main camera
        if (_quadRenderer != null && Camera.main == null)
            _quadRenderer.enabled = false;
    }

    #region Panel

    private void EnsurePanelSettingsAssigned()
    {
        if (_document == null || _panelSettings != null)
            return;

        PanelSettings template = FindTemplatePanelSettings();
        if (template == null)
            return; // try again next frame

        // Own copy of the UI's panel settings (same scaling, so ripple positions from other
        // panels line up), drawing into the render texture when we can composite it
        _panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
        _panelSettings.name = "PixelBlastBackground Panel";
        _panelSettings.hideFlags = HideFlags.DontSave;
        _panelSettings.themeStyleSheet = template.themeStyleSheet;
        _panelSettings.textSettings = template.textSettings;
        _panelSettings.scaleMode = template.scaleMode;
        _panelSettings.scale = template.scale;
        _panelSettings.referenceResolution = template.referenceResolution;
        _panelSettings.screenMatchMode = template.screenMatchMode;
        _panelSettings.match = template.match;
        _panelSettings.referenceDpi = template.referenceDpi;
        _panelSettings.fallbackDpi = template.fallbackDpi;
        _panelSettings.referenceSpritePixelsPerUnit = template.referenceSpritePixelsPerUnit;
        _panelSettings.sortingOrder = BackgroundPanelSortingOrder;

        if (_material != null)
        {
            _panelSettings.clearColor = true;
            _panelSettings.colorClearValue = Color.clear;
            _panelSettings.targetTexture = EnsureTextureSize();

            // Nothing on this panel takes input; FeedPointer drives the trail instead
            _panelSettings.SetScreenToPanelSpaceFunction(_ => new Vector2(float.NaN, float.NaN));
        }

        _document.panelSettings = _panelSettings;
    }

    private PanelSettings FindTemplatePanelSettings()
    {
        UIDocument[] docs = FindObjectsByType<UIDocument>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < docs.Length; i++)
        {
            if (docs[i] != _document && docs[i].panelSettings != null)
                return docs[i].panelSettings;
        }

#if UNITY_EDITOR
        return UnityEditor.AssetDatabase.LoadAssetAtPath<PanelSettings>("Assets/UI/MenuPanelSettings.asset");
#else
        return null;
#endif
    }

    private void EnsureBlastElement()
    {
        if (_document == null || _document.rootVisualElement == null)
            return;

        VisualElement root = _document.rootVisualElement;
        root.pickingMode = PickingMode.Ignore;
        root.style.position = Position.Absolute;
        root.style.left = 0;
        root.style.top = 0;
        root.style.right = 0;
        root.style.bottom = 0;
        root.style.width = Length.Percent(100);
        root.style.height = Length.Percent(100);

        if (_blast == null || _blast.parent != root)
        {
            root.Clear();
            _blast = new PixelBlastBackground
            {
                name = "persistent-pixel-blast",
                pixelSize = 10f,
                patternDensity = 4f,
                glow = 0.5f,
                drift = 0.1f,
                hoverRadius = 50f,
                rippleThickness = 0.4f,
                accentColor = _accentColor,
                pickingMode = PickingMode.Ignore,
                listenToPanelPointer = false
            };
            _blast.style.position = Position.Absolute;
            _blast.style.left = 0;
            _blast.style.top = 0;
            _blast.style.right = 0;
            _blast.style.bottom = 0;
            _blast.style.opacity = BackgroundOpacity;

            root.Add(_blast);
        }
    }

    /// <summary>Hover trail and click ripples from the mouse / primary touch.</summary>
    private void FeedPointer()
    {
        if (_blast == null || _blast.panel == null)
            return;

        Pointer pointer = Pointer.current;
        if (pointer == null || !Application.isFocused)
        {
            _blast.ClearPointer();
            return;
        }

        Vector2 screen = pointer.position.ReadValue();
        if (screen.x < 0f || screen.y < 0f || screen.x > Screen.width || screen.y > Screen.height)
        {
            _blast.ClearPointer();
            return;
        }

        Rect panelRect = _blast.panel.visualTree.layout;
        if (float.IsNaN(panelRect.width) || panelRect.width <= 0f || Screen.width <= 0 || Screen.height <= 0)
            return;

        // Screen space is bottom-up in pixels; panel space is top-down in panel units
        Vector2 panelPosition = new(
            screen.x * panelRect.width / Screen.width,
            (Screen.height - screen.y) * panelRect.height / Screen.height
        );
        Vector2 local = _blast.WorldToLocal(panelPosition);

        _blast.SetPointer(local);

        if (pointer.press.wasPressedThisFrame)
            _blast.AddRipple(local);
    }

    #endregion

    #region Backdrop Quad

    private void CreateBackdropQuad()
    {
        Shader shader = Resources.Load<Shader>(CompositeShaderResource);
        if (shader == null)
            shader = Shader.Find(CompositeShaderName);

        if (shader == null || !shader.isSupported)
        {
            Debug.LogWarning(
                "PersistentPixelBlastBackground: composite shader missing, so the background " +
                "falls back to a UI overlay (it will draw over world sprites)."
            );
            return;
        }

        _material = new Material(shader)
        {
            name = "PixelBlastBackground Composite",
            hideFlags = HideFlags.DontSave
        };

        _quadMesh = new Mesh
        {
            name = "PixelBlastBackground Quad",
            hideFlags = HideFlags.DontSave,
            vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f)
            },
            uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f)
            },
            triangles = new[] { 0, 2, 1, 2, 3, 1 }
        };
        _quadMesh.bounds = new Bounds(Vector3.zero, new Vector3(1f, 1f, 0.01f));

        GameObject quad = new("PixelBlastBackground Backdrop");
        quad.transform.SetParent(transform, false);
        quad.AddComponent<MeshFilter>().sharedMesh = _quadMesh;

        _quadRenderer = quad.AddComponent<MeshRenderer>();
        _quadRenderer.sharedMaterial = _material;
        _quadRenderer.shadowCastingMode = ShadowCastingMode.Off;
        _quadRenderer.receiveShadows = false;
        _quadRenderer.lightProbeUsage = LightProbeUsage.Off;
        _quadRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

        // Bottom sorting layer, lowest order: behind every sprite
        SortingLayer[] layers = SortingLayer.layers;
        _quadRenderer.sortingLayerID = layers.Length > 0 ? layers[0].id : 0;
        _quadRenderer.sortingOrder = short.MinValue;
        _quadRenderer.enabled = false;

        EnsureTextureSize();
    }

    /// <summary>Keeps the render texture the size of the screen (recreated on resize).</summary>
    private RenderTexture EnsureTextureSize()
    {
        if (_material == null)
            return null;

        int width = Mathf.Max(1, Screen.width);
        int height = Mathf.Max(1, Screen.height);

        if (_texture != null && _texture.width == width && _texture.height == height)
            return _texture;

        RenderTexture old = _texture;

        _texture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
        {
            name = "PixelBlastBackground",
            hideFlags = HideFlags.DontSave
        };
        _texture.Create();

        _material.mainTexture = _texture;

        if (_panelSettings != null)
            _panelSettings.targetTexture = _texture;

        if (old != null)
        {
            old.Release();
            DestroyOwned(old);
        }

        return _texture;
    }

    /// <summary>Fits the quad to the main camera's view, just before that camera renders.</summary>
    private void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
    {
        if (_quadRenderer == null)
            return;

        // Only the main camera places it; other cameras (Scene view, previews) see it wherever it is
        if (cam == null || cam != Camera.main || _texture == null)
            return;

        Transform camTransform = cam.transform;
        float distance = Mathf.Lerp(cam.nearClipPlane, cam.farClipPlane, 0.9f);

        float height = cam.orthographic
            ? cam.orthographicSize * 2f
            : 2f * distance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float width = height * cam.aspect;

        Transform quad = _quadRenderer.transform;
        quad.SetPositionAndRotation(
            camTransform.position + camTransform.forward * distance,
            camTransform.rotation
        );
        quad.localScale = new Vector3(width, height, 1f);

        _quadRenderer.enabled = true;
    }

    #endregion

    /// <summary>Updates the accent colour of the persistent PixelBlast background.</summary>
    public void SetAccentColor(Color color)
    {
        _accentColor = color;
        EnsureBlastElement();

        if (_blast != null)
            _blast.accentColor = color;
    }

    /// <summary>Triggers a ripple at the given panel-space position.</summary>
    public void AddRipple(Vector2 panelPosition)
    {
        EnsureBlastElement();

        if (_blast != null)
            _blast.AddRipple(_blast.WorldToLocal(panelPosition));
    }

    private static void DestroyOwned(Object obj)
    {
        if (obj == null)
            return;

        if (Application.isPlaying)
            Destroy(obj);
        else
            DestroyImmediate(obj);
    }
}
