using System;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// UI Toolkit port of the frontend's HueBar. A solid block of the current
/// colour; click once to open it into a white -> rainbow gradient, then the
/// handle follows the mouse (no need to hold). Click the bar (or drag and
/// release) to confirm; click outside or press Escape to cancel.
/// </summary>
[UxmlElement]
public partial class HueBar : VisualElement
{
    public const string UssClassName = "hue-bar";
    public const string HandleUssClassName = "hue-bar__handle";

    // Same stops as the frontend: white, red, yellow, lime, cyan, blue,
    // magenta, red. White fades into red over the first of 7 segments.
    private const float WhiteEnd = 1f / 7f;
    private const float KeyStep = 0.02f;
    private const float DragThreshold = 4f;

    private static Texture2D _gradient;

    private readonly VisualElement _handle;

    private float? _value;
    private bool _open;
    private bool _picking;
    private bool _openedOnPress;
    private bool _dragged;
    private float _startX;
    private float? _previousValue;
    private VisualElement _panelRoot;

    /// <summary>Position 0-1 along the bar, null = nothing picked yet.</summary>
    public float? Value
    {
        get => _value;
        set
        {
            float? clamped = value.HasValue ? Mathf.Clamp01(value.Value) : null;

            if (Nullable.Equals(clamped, _value))
                return;

            _value = clamped;
            Refresh();
            ValueChanged?.Invoke(_value);
        }
    }

    public bool IsOpen => _open;

    /// <summary>The colour for the current value (white when unset).</summary>
    public Color Color => _value.HasValue ? ColorAt(_value.Value) : Color.white;

    public event Action<float?> ValueChanged;

    public HueBar()
    {
        AddToClassList(UssClassName);
        focusable = true;

        _handle = new VisualElement { pickingMode = PickingMode.Ignore };
        _handle.AddToClassList(HandleUssClassName);
        Add(_handle);

        RegisterCallback<PointerDownEvent>(OnPointerDown);
        RegisterCallback<PointerMoveEvent>(OnPointerMove);
        RegisterCallback<PointerUpEvent>(OnPointerUp);
        RegisterCallback<PointerCaptureOutEvent>(_ => _picking = false);
        RegisterCallback<KeyDownEvent>(OnKeyDown);
        RegisterCallback<AttachToPanelEvent>(OnAttach);
        RegisterCallback<DetachFromPanelEvent>(OnDetach);

        Refresh();
    }

    /// <summary>
    /// Colour at a position (0-1) along the bar, matching the gradient
    /// exactly: white -> red over the first segment, then the hue wheel.
    /// </summary>
    public static Color ColorAt(float pos)
    {
        pos = Mathf.Clamp01(pos);

        if (pos < WhiteEnd)
        {
            float white = 1f - pos / WhiteEnd;
            return new Color(1f, white, white);
        }

        float hue = (pos - WhiteEnd) / (1f - WhiteEnd);
        return Color.HSVToRGB(Mathf.Repeat(hue, 1f), 1f, 1f);
    }

    #region Pointer

    private void OnPointerDown(PointerDownEvent evt)
    {
        if (!_open)
        {
            _previousValue = _value;
            _openedOnPress = true;
            SetOpen(true);
        }
        else
        {
            _openedOnPress = false;
        }

        _dragged = false;
        _startX = evt.position.x;
        _picking = true;

        Focus();
        this.CapturePointer(evt.pointerId);
        Value = PositionAt(evt.position.x);

        evt.StopPropagation();
    }

    private void OnPointerMove(PointerMoveEvent evt)
    {
        if (!_picking)
            return;

        if (Mathf.Abs(evt.position.x - _startX) > DragThreshold)
            _dragged = true;

        Value = PositionAt(evt.position.x);
    }

    private void OnPointerUp(PointerUpEvent evt)
    {
        if (!_picking)
            return;

        _picking = false;
        this.ReleasePointer(evt.pointerId);

        // First click only opens it; the bar stays open and follows the
        // mouse until the next click (or a drag) confirms
        if (_openedOnPress && !_dragged)
        {
            _openedOnPress = false;
            return;
        }

        SetOpen(false);
    }

    // While open, the handle snaps to the mouse anywhere on screen
    private void OnPanelPointerMove(PointerMoveEvent evt)
    {
        if (!_open || _picking || evt.pointerType != UnityEngine.UIElements.PointerType.mouse)
            return;

        Value = PositionAt(evt.position.x);
    }

    private void OnPanelPointerDown(PointerDownEvent evt)
    {
        if (!_open || worldBound.Contains(evt.position))
            return;

        Cancel();
    }

    private void OnKeyDown(KeyDownEvent evt)
    {
        switch (evt.keyCode)
        {
            case KeyCode.Escape:
                if (_open)
                {
                    Cancel();
                    evt.StopPropagation();
                }
                return;

            case KeyCode.RightArrow:
            case KeyCode.UpArrow:
                Value = (_value ?? 0f) + KeyStep;
                evt.StopPropagation();
                return;

            case KeyCode.LeftArrow:
            case KeyCode.DownArrow:
                Value = (_value ?? 0f) - KeyStep;
                evt.StopPropagation();
                return;
        }
    }

    private float PositionAt(float panelX)
    {
        Rect bounds = worldBound;

        return bounds.width > 0f
            ? Mathf.Clamp01((panelX - bounds.xMin) / bounds.width)
            : 0f;
    }

    #endregion

    #region State

    /// <summary>Closes without picking, restoring the value from before.</summary>
    public void Cancel()
    {
        _picking = false;
        Value = _previousValue;
        SetOpen(false);
    }

    private void SetOpen(bool open)
    {
        _open = open;
        Refresh();
    }

    private void Refresh()
    {
        if (_open)
        {
            style.backgroundImage = new StyleBackground(GetGradient());
            style.backgroundSize = new BackgroundSize(
                Length.Percent(100),
                Length.Percent(100)
            );
            style.backgroundColor = Color.clear;
        }
        else
        {
            style.backgroundImage = StyleKeyword.None;
            style.backgroundColor = Color;
        }

        bool showHandle = _open && _value.HasValue;
        _handle.style.display = showHandle ? DisplayStyle.Flex : DisplayStyle.None;

        if (showHandle)
            _handle.style.left = Length.Percent(_value.Value * 100f);
    }

    private static Texture2D GetGradient()
    {
        if (_gradient != null)
            return _gradient;

        const int width = 7 * 64;

        _gradient = new Texture2D(width, 1, TextureFormat.RGBA32, false)
        {
            name = "HueBarGradient",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave
        };

        Color[] pixels = new Color[width];

        for (int i = 0; i < width; i++)
            pixels[i] = ColorAt(i / (width - 1f));

        _gradient.SetPixels(pixels);
        _gradient.Apply(false, true);

        return _gradient;
    }

    #endregion

    #region Panel

    private void OnAttach(AttachToPanelEvent evt)
    {
        _panelRoot = evt.destinationPanel.visualTree;
        _panelRoot.RegisterCallback<PointerMoveEvent>(OnPanelPointerMove, TrickleDown.TrickleDown);
        _panelRoot.RegisterCallback<PointerDownEvent>(OnPanelPointerDown, TrickleDown.TrickleDown);
    }

    private void OnDetach(DetachFromPanelEvent evt)
    {
        if (_panelRoot == null)
            return;

        _panelRoot.UnregisterCallback<PointerMoveEvent>(OnPanelPointerMove, TrickleDown.TrickleDown);
        _panelRoot.UnregisterCallback<PointerDownEvent>(OnPanelPointerDown, TrickleDown.TrickleDown);
        _panelRoot = null;
    }

    #endregion
}
