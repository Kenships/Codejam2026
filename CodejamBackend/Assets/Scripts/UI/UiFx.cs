using Core.AudioSystem;
using PrimeTween;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Small, shared UI motion helpers so every screen reacts the same way:
/// a quick pop when a value changes, a shake when something can't change,
/// and <see cref="TimerWarning"/> for the last seconds of a countdown.
/// </summary>
public static class UiFx
{
    /// <summary>Tailwind red-400, the same warning red the phones use for low time.</summary>
    public static readonly Color WarningColor = new(0.973f, 0.443f, 0.443f);

    /// <summary>Overshoot scale pop (e.g. 1.18x back to 1x) for "this just changed".</summary>
    public static void Pop(VisualElement element, float from = 1.18f, float duration = 0.35f)
    {
        if (element == null)
            return;

        Tween.Custom(
            target: element,
            startValue: from,
            endValue: 1f,
            duration: duration,
            onValueChange: (el, s) => el.style.scale = new Scale(new Vector3(s, s, 1f)),
            ease: Ease.OutBack
        );
    }

    /// <summary>Short side-to-side shake for "can't go any further".</summary>
    public static void Shake(VisualElement element, float pixels = 6f, float duration = 0.3f)
    {
        if (element == null)
            return;

        Tween.Custom(
            target: element,
            startValue: 1f,
            endValue: 0f,
            duration: duration,
            onValueChange: (el, t) =>
                el.style.translate = new Translate(Mathf.Sin(t * Mathf.PI * 6f) * pixels * t, 0f),
            ease: Ease.Linear
        );
    }

    /// <summary>
    /// Low-time treatment for a countdown label: from <c>warnFrom</c> seconds left the number
    /// turns red and pops each second; from <c>tickFrom</c> seconds left a soft tick plays.
    /// Call <see cref="Update"/> whenever the remaining time changes.
    /// </summary>
    public sealed class TimerWarning
    {
        private readonly Label _label;
        private readonly int _warnFrom;
        private readonly int _tickFrom;
        private int _lastSecond = -1;

        public bool IsWarning { get; private set; }

        public TimerWarning(Label label, int warnFrom = 10, int tickFrom = 3)
        {
            _label = label;
            _warnFrom = warnFrom;
            _tickFrom = tickFrom;
        }

        public void Update(float remainingSeconds)
        {
            if (_label == null)
                return;

            int second = Mathf.Max(0, Mathf.CeilToInt(remainingSeconds));
            bool warning = second > 0 && second <= _warnFrom;

            if (warning != IsWarning)
            {
                IsWarning = warning;
                if (!warning)
                    _label.style.color = StyleKeyword.Null; // back to the screen's own colour
            }

            ReapplyColor();

            // Only count down, never react to the first reading or to the timer being reset upward
            if (second != _lastSecond)
            {
                bool countingDown = _lastSecond > second;
                _lastSecond = second;

                if (countingDown && warning)
                {
                    Pop(_label, 1.15f, 0.3f);

                    if (second <= _tickFrom)
                        GameAudioController.EnsureExists().PlayCountdownTick(second);
                }
            }
        }

        /// <summary>Screens that recolour their text every frame call this afterwards.</summary>
        public void ReapplyColor()
        {
            if (IsWarning && _label != null)
                _label.style.color = WarningColor;
        }

        /// <summary>For a new round: forget the last second so the next countdown starts clean.</summary>
        public void Reset()
        {
            _lastSecond = -1;
            if (IsWarning && _label != null)
                _label.style.color = StyleKeyword.Null;
            IsWarning = false;
        }
    }
}
