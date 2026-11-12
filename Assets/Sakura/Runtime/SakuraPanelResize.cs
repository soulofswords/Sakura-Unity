using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public static class SakuraPanelResize
    {
        public static void Attach(VisualElement panel)
        {
            foreach (string edge in new[] { "top", "bottom", "left", "right" })
            {
                string side = edge;
                var handle = new Label(side == "top" || side == "bottom" ? "···" : "⋮") { focusable = true, tooltip = "Resize panel " + side };
                handle.AddToClassList("resize-handle"); handle.AddToClassList("resize-" + side); panel.Add(handle);
                Rect start = default; Vector2 point = default; bool dragging = false;
                handle.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    start = panel.layout; point = evt.position; dragging = true;
                    handle.CapturePointer(evt.pointerId); evt.StopPropagation();
                });
                handle.RegisterCallback<PointerMoveEvent>(evt =>
                {
                    if (!dragging || !handle.HasPointerCapture(evt.pointerId)) return;
                    Vector2 delta = (Vector2)evt.position - point;
                    Apply(panel, side, start, delta); evt.StopPropagation();
                });
                handle.RegisterCallback<PointerUpEvent>(evt => { dragging = false; handle.ReleasePointer(evt.pointerId); evt.StopPropagation(); });
                handle.RegisterCallback<PointerCaptureOutEvent>(evt => dragging = false);
                handle.RegisterCallback<KeyDownEvent>(evt =>
                {
                    Vector2 delta = evt.keyCode == KeyCode.LeftArrow ? Vector2.left * 16 : evt.keyCode == KeyCode.RightArrow ? Vector2.right * 16 : evt.keyCode == KeyCode.UpArrow ? Vector2.up * -16 : evt.keyCode == KeyCode.DownArrow ? Vector2.up * 16 : Vector2.zero;
                    if (delta == Vector2.zero) return;
                    Apply(panel, side, panel.layout, delta); evt.StopPropagation();
                });
            }
        }

        public static void Apply(VisualElement panel, string side, Rect start, Vector2 delta)
        {
            var bounds = panel.parent.layout;
            float minWidth = Mathf.Min(300, bounds.width - 100);
            float minHeight = Mathf.Min(260, bounds.height - 30);
            if (side == "left") panel.style.width = Mathf.Clamp(start.width - delta.x, minWidth, Mathf.Max(minWidth, start.xMax - 90));
            if (side == "right")
            {
                float width = Mathf.Clamp(start.width + delta.x, minWidth, Mathf.Max(minWidth, bounds.width - start.x - 10));
                panel.style.width = width; panel.style.right = bounds.width - start.x - width;
            }
            if (side == "top") panel.style.top = Mathf.Clamp(start.y + delta.y, 12, Mathf.Max(12, start.yMax - minHeight));
            if (side == "bottom") panel.style.bottom = Mathf.Clamp(bounds.height - start.yMax - delta.y, 12, Mathf.Max(12, bounds.height - start.y - minHeight));
        }
    }
}
