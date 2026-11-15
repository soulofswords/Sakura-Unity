using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    /// <summary>Slider with the filled portion the reference app draws from the minimum to the thumb.</summary>
    public sealed class SakuraSlider : Slider
    {
        private VisualElement fill, gap;

        public SakuraSlider(float start, float end) : base(start, end) { Initialize(); }
        public SakuraSlider(string label, float start, float end) : base(label, start, end) { Initialize(); }

        private void Initialize()
        {
            AddToClassList("sakura-slider");
            fill = new VisualElement { pickingMode = PickingMode.Ignore };
            fill.AddToClassList("sakura-slider-fill");
            gap = new VisualElement { pickingMode = PickingMode.Ignore };
            gap.AddToClassList("sakura-slider-gap");
            RegisterCallback<AttachToPanelEvent>(_ => AttachFill());
            RegisterCallback<ChangeEvent<float>>(_ => UpdateFill());
        }

        private void AttachFill()
        {
            var tracker = this.Q(className: "unity-base-slider__tracker");
            if (tracker == null) return;
            if (fill.parent != tracker) { fill.RemoveFromHierarchy(); tracker.Insert(0, fill); }
            if (gap.parent != tracker) { gap.RemoveFromHierarchy(); tracker.Insert(1, gap); }
            tracker.RegisterCallback<GeometryChangedEvent>(_ => UpdateFill());
            UpdateFill();
        }

        private void UpdateFill()
        {
            if (fill == null) return;
            float span = Mathf.Max(1e-6f, highValue - lowValue);
            float ratio = Mathf.Clamp01((value - lowValue) / span);
            fill.style.width = new Length(ratio * 100, LengthUnit.Percent);
            var tracker = fill.parent;
            float width = tracker?.layout.width ?? 0;
            bool showGap = width > 1 && ratio > .001f && ratio < .999f;
            gap.style.display = showGap ? DisplayStyle.Flex : DisplayStyle.None;
            if (showGap)
            {
                // The reference has a clean gap on both sides of the round thumb.
                // The gap sits under the dragger, so it reads as background rather
                // than as a second colour on the track.
                const float gapWidth = 42f;
                gap.style.width = gapWidth;
                gap.style.left = Mathf.Clamp(ratio * width - gapWidth / 2, 0, Mathf.Max(0, width - gapWidth));
            }
        }
    }
}
