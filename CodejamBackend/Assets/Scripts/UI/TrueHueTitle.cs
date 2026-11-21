using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// UI Toolkit port of frontend/src/components/TrueHueTitle.tsx.
/// - When OverlapAnimate is false (Create Game / title screen): renders static "True Hue".
/// - When OverlapAnimate is true (Host Lobby / waiting screen): overlaps "True" and "hue"
///   and smoothly shifts between them on the same 3.6s hold / 1.4s shift cycle as the web UI.
/// </summary>
[UxmlElement]
public partial class TrueHueTitle : VisualElement
{
    private const float HoldDuration = 3.6f;
    private const float ShiftDuration = 1.4f;

    private enum ShiftStage
    {
        True,
        ShiftToHue,
        Hue,
        ShiftToTrue
    }

    private readonly Label _staticLabel;
    private readonly VisualElement _animatedRow;
    private readonly Label _tLabel;
    private readonly VisualElement _rhSlot;
    private readonly Label _rLabel;
    private readonly Label _hLabel;
    private readonly Label _ueLabel;

    private IVisualElementScheduledItem _tick;
    private bool _overlapAnimate;
    private Color _baseColor = Color.white;
    private ShiftStage _stage = ShiftStage.True;
    private float _stageElapsed;
    private float _lastTime = -1f;

    [UxmlAttribute]
    public bool OverlapAnimate
    {
        get => _overlapAnimate;
        set
        {
            if (_overlapAnimate == value)
                return;

            _overlapAnimate = value;
            _stage = ShiftStage.True;
            _stageElapsed = 0f;
            _lastTime = -1f;
            RefreshMode();
        }
    }

    public Color BaseColor
    {
        get => _baseColor;
        set
        {
            _baseColor = value;
            ApplyVisualState();
        }
    }

    public TrueHueTitle()
    {
        AddToClassList("true-hue-title");
        pickingMode = PickingMode.Ignore;

        // Static "True Hue" label
        _staticLabel = new Label("True Hue")
        {
            name = "true-hue-static",
            pickingMode = PickingMode.Ignore
        };
        _staticLabel.AddToClassList("menu-title");
        _staticLabel.AddToClassList("true-hue-title__static");
        Add(_staticLabel);

        // Animated overlapping "True" <-> "hue" row
        _animatedRow = new VisualElement
        {
            name = "true-hue-animated",
            pickingMode = PickingMode.Ignore
        };
        _animatedRow.AddToClassList("true-hue-title__animated");

        _tLabel = CreateLetterLabel("T", "true-hue-title__t");

        _rhSlot = new VisualElement
        {
            name = "true-hue-rh-slot",
            pickingMode = PickingMode.Ignore
        };
        _rhSlot.AddToClassList("true-hue-title__rh-slot");

        // "h" sits in flow so the slot takes "h"'s width; "r" overlays at left: 0
        _hLabel = CreateLetterLabel("h", "true-hue-title__h");
        _rLabel = CreateLetterLabel("r", "true-hue-title__r");
        _rhSlot.Add(_hLabel);
        _rhSlot.Add(_rLabel);

        _ueLabel = CreateLetterLabel("ue", "true-hue-title__ue");

        _animatedRow.Add(_tLabel);
        _animatedRow.Add(_rhSlot);
        _animatedRow.Add(_ueLabel);
        Add(_animatedRow);

        RegisterCallback<AttachToPanelEvent>(OnAttach);
        RegisterCallback<DetachFromPanelEvent>(OnDetach);

        RefreshMode();
    }

    private static Label CreateLetterLabel(string text, string extraClass)
    {
        Label label = new(text)
        {
            pickingMode = PickingMode.Ignore
        };
        label.AddToClassList("menu-title");
        label.AddToClassList("true-hue-title__letter");
        label.AddToClassList(extraClass);
        return label;
    }

    private void OnAttach(AttachToPanelEvent evt)
    {
        _lastTime = -1f;
        _tick = schedule.Execute(Tick).Every(16);
        RefreshMode();
    }

    private void OnDetach(DetachFromPanelEvent evt)
    {
        _tick?.Pause();
        _tick = null;
    }

    private void RefreshMode()
    {
        _staticLabel.style.display = _overlapAnimate ? DisplayStyle.None : DisplayStyle.Flex;
        _animatedRow.style.display = _overlapAnimate ? DisplayStyle.Flex : DisplayStyle.None;

        if (_overlapAnimate)
            _tick?.Resume();
        else
            _tick?.Pause();

        ApplyVisualState();
    }

    private void Tick()
    {
        if (!_overlapAnimate)
            return;

        float now = Time.realtimeSinceStartup;
        float dt = _lastTime < 0f ? 0f : Mathf.Clamp(now - _lastTime, 0f, 0.1f);
        _lastTime = now;

        _stageElapsed += dt;

        float stageDuration = _stage is ShiftStage.True or ShiftStage.Hue
            ? HoldDuration
            : ShiftDuration;

        if (_stageElapsed >= stageDuration)
        {
            _stageElapsed -= stageDuration;
            _stage = _stage switch
            {
                ShiftStage.True => ShiftStage.ShiftToHue,
                ShiftStage.ShiftToHue => ShiftStage.Hue,
                ShiftStage.Hue => ShiftStage.ShiftToTrue,
                _ => ShiftStage.True
            };
        }

        ApplyVisualState();
    }

    private void ApplyVisualState()
    {
        _staticLabel.style.color = _baseColor;

        if (!_overlapAnimate)
            return;

        // hueBlend: 0 = fully "True", 1 = fully "hue"
        float hueBlend = _stage switch
        {
            ShiftStage.True => 0f,
            ShiftStage.Hue => 1f,
            ShiftStage.ShiftToHue => SmoothInOut(Mathf.Clamp01(_stageElapsed / ShiftDuration)),
            ShiftStage.ShiftToTrue => 1f - SmoothInOut(Mathf.Clamp01(_stageElapsed / ShiftDuration)),
            _ => 0f
        };

        float shiftIntensity = _stage is ShiftStage.ShiftToHue or ShiftStage.ShiftToTrue
            ? Mathf.Sin(Mathf.Clamp01(_stageElapsed / ShiftDuration) * Mathf.PI)
            : 0f;

        // Horizontal centering shift (-0.22em when "hue" is showing)
        float fontSize = _ueLabel.resolvedStyle.fontSize;
        if (fontSize <= 1f)
            fontSize = 76f;

        float shiftX = Mathf.Lerp(0f, -0.22f * fontSize, hueBlend);
        _animatedRow.style.translate = new Translate(Length.Pixels(shiftX), Length.Pixels(0f));

        // "T" fades out & lifts slightly when shifting to "hue"
        _tLabel.style.opacity = 1f - hueBlend;
        _tLabel.style.translate = new Translate(Length.Pixels(0f), Length.Pixels(Mathf.Lerp(0f, -6f, hueBlend)));

        // "r" visible for "True", "h" visible for "hue"
        _rLabel.style.opacity = 1f - hueBlend;
        _hLabel.style.opacity = hueBlend;

        // Subtle vertical reveal on "h" ascender as it transitions in
        _hLabel.style.translate = new Translate(Length.Pixels(0f), Length.Pixels(Mathf.Lerp(4f, 0f, hueBlend)));

        // Neighbouring hue colour shifts during transition (matching TrueHueTitle.tsx)
        Color leadColor = ShiftHue(_baseColor, 35f);
        Color trailColor = ShiftHue(_baseColor, -35f);
        Color midColor = ShiftHue(_baseColor, 18f);

        Color tTarget = _stage switch
        {
            ShiftStage.ShiftToHue => leadColor,
            ShiftStage.ShiftToTrue => trailColor,
            _ => _baseColor
        };
        Color rTarget = _stage == ShiftStage.ShiftToHue ? leadColor : _baseColor;
        Color hTarget = _stage switch
        {
            ShiftStage.ShiftToHue => trailColor,
            ShiftStage.ShiftToTrue => leadColor,
            _ => _baseColor
        };

        _tLabel.style.color = Color.Lerp(_baseColor, tTarget, shiftIntensity);
        _rLabel.style.color = Color.Lerp(_baseColor, rTarget, shiftIntensity);
        _hLabel.style.color = Color.Lerp(_baseColor, hTarget, shiftIntensity);
        _ueLabel.style.color = Color.Lerp(_baseColor, midColor, shiftIntensity);
    }

    private static Color ShiftHue(Color color, float degrees)
    {
        Color.RGBToHSV(color, out float h, out float s, out float v);
        if (s < 0.05f)
            return color;

        return Color.HSVToRGB(Mathf.Repeat(h + degrees / 360f, 1f), s, v);
    }

    private static float SmoothInOut(float t) => t * t * (3f - 2f * t);
}
