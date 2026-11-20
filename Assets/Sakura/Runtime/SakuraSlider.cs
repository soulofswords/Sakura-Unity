using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    /// <summary>Slider with the filled portion the reference app draws from the minimum to the thumb.</summary>
    public sealed class SakuraSlider : Slider
    {
        private VisualElement fill;

        public SakuraSlider(float start, float end) : base(start, end) { Initialize(); }
        public SakuraSlider(string label, float start, float end) : base(label, start, end) { Initialize(); }

        private void Initialize()
        {
            AddToClassList("sakura-slider");
            fill = new VisualElement { pickingMode = PickingMode.Ignore };
            fill.AddToClassList("sakura-slider-fill");
            RegisterCallback<AttachToPanelEvent>(_ => AttachFill());
            RegisterCallback<ChangeEvent<float>>(_ => UpdateFill());
        }

        private void AttachFill()
        {
            var tracker = this.Q(className: "unity-base-slider__tracker");
            if (tracker == null) return;
            if (fill.parent != tracker) { fill.RemoveFromHierarchy(); tracker.Insert(0, fill); }
            tracker.RegisterCallback<GeometryChangedEvent>(_ => UpdateFill());
            UpdateFill();
        }

        private void UpdateFill()
        {
            if (fill == null) return;
            float span = Mathf.Max(1e-6f, highValue - lowValue);
            float ratio = Mathf.Clamp01((value - lowValue) / span);
            fill.style.width = new Length(ratio * 100, LengthUnit.Percent);
        }
    }
}
