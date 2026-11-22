using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// A slanted streak of light that sweeps across its parent every <see cref="Interval"/> seconds
/// while <see cref="Active"/> (e.g. the lobby's START MATCH button once enough players joined).
/// Put it inside the element to shine; the parent needs overflow: hidden to clip the streak.
/// </summary>
[UxmlElement]
public partial class GlintOverlay : VisualElement
{
    private readonly VisualElement _streak;
    private IVisualElementScheduledItem _tick;
    private bool _active;
    private float _cycleStart = -1f;

    /// <summary>Seconds between the starts of two sweeps.</summary>
    [UxmlAttribute]
    public float Interval { get; set; } = 2.4f;

    /// <summary>How long one sweep takes to cross.</summary>
    [UxmlAttribute]
    public float SweepDuration { get; set; } = 0.6f;

    [UxmlAttribute]
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value)
                return;

            _active = value;
            // Sweep right away when it turns on, so it reads as "ready now"
            _cycleStart = -1f;
            _streak.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }

    public GlintOverlay()
    {
        AddToClassList("glint-overlay");
        pickingMode = PickingMode.Ignore;

        _streak = new VisualElement { pickingMode = PickingMode.Ignore };
        _streak.AddToClassList("glint-overlay__streak");
        _streak.style.display = DisplayStyle.None;
        Add(_streak);

        RegisterCallback<AttachToPanelEvent>(_ =>
        {
            _tick ??= schedule.Execute(Tick).Every(16);
            _tick.Resume();
        });
        RegisterCallback<DetachFromPanelEvent>(_ => _tick?.Pause());
    }

    private void Tick()
    {
        if (!_active)
            return;

        float now = Time.realtimeSinceStartup;
        if (_cycleStart < 0f)
            _cycleStart = now;

        float width = resolvedStyle.width;
        float streakWidth = _streak.resolvedStyle.width;
        if (width < 1f || float.IsNaN(streakWidth))
            return;

        float t = Mathf.Repeat(now - _cycleStart, Mathf.Max(Interval, SweepDuration));
        float progress = Mathf.Clamp01(t / Mathf.Max(0.05f, SweepDuration));
        // Ease in-out so the streak speeds through the middle of the button
        float eased = progress * progress * (3f - 2f * progress);

        // From fully off the left edge to fully off the right (extra room for the slant)
        float from = -streakWidth * 2f;
        float to = width + streakWidth;
        _streak.style.translate = new Translate(Mathf.Lerp(from, to, eased), 0f);
        _streak.style.opacity = progress >= 1f ? 0f : 1f;
    }
}
