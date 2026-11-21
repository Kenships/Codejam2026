using Core.Utility;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

/// <summary>
/// Persistent full-screen PixelBlastBackground that lives across all scenes and pages
/// (Menu, Lobby, Drawing, Guessing, Reveal, Finished) without resetting its drift or
/// ripple simulation. Renders behind all other UIDocuments via sortingOrder = -100.
/// </summary>
public class PersistentPixelBlastBackground : Singleton<PersistentPixelBlastBackground>
{
    private const float BackgroundOpacity = 0.6f;
    private const float BackgroundSortingOrder = -100f;

    private UIDocument _document;
    private PixelBlastBackground _blast;
    private Color _accentColor = Color.white;

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

        SetupDocument();
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    protected override void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
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
        EnsureBlastElement();

        if (_blast != null)
            _blast.ForceTick();
    }

    private void SetupDocument()
    {
        _document = GetComponent<UIDocument>();
        if (_document == null)
            _document = gameObject.AddComponent<UIDocument>();

        _document.sortingOrder = BackgroundSortingOrder;
        EnsurePanelSettingsAssigned();
        EnsureBlastElement();
    }

    private void EnsurePanelSettingsAssigned()
    {
        if (_document == null || _document.panelSettings != null)
            return;

        // Reuse the PanelSettings from any UIDocument already in the scene (works in Editor and Builds)
        UIDocument[] docs = FindObjectsByType<UIDocument>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < docs.Length; i++)
        {
            if (docs[i] != _document && docs[i].panelSettings != null)
            {
                _document.panelSettings = docs[i].panelSettings;
                break;
            }
        }

#if UNITY_EDITOR
        if (_document.panelSettings == null)
        {
            _document.panelSettings = UnityEditor.AssetDatabase.LoadAssetAtPath<PanelSettings>(
                "Assets/UI/MenuPanelSettings.asset"
            );
        }
#endif
    }

    private void EnsureBlastElement()
    {
        if (_document == null || _document.rootVisualElement == null)
            return;

        VisualElement root = _document.rootVisualElement;
        // PickingMode.Position on the -100 background ensures UI Toolkit always hit-tests the panel
        // and dispatches PointerMoveEvent / PointerDownEvent for hover trails & ripples even when
        // foreground overlays (GuessingPhase / RevealUI) use PickingMode.Ignore.
        root.pickingMode = PickingMode.Position;
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
                pickingMode = PickingMode.Position
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
}
