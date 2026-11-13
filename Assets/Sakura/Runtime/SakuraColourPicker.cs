using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraColourPicker
    {
        private readonly Texture2D texture;
        private readonly VisualElement preview, hueMarker, squareMarker;
        private readonly IntegerField red, green, blue;
        private readonly Action<Color> changed;
        private float hue, saturation, brightness;
        private bool ringDrag;
        private const int Size = 200, SquareMin = 46, SquareMax = 154;

        public SakuraColourPicker(VisualElement parent, Color initial, Action<Color> changed)
        {
            this.changed = changed;
            Color.RGBToHSV(initial,out hue,out saturation,out brightness);
            preview = new VisualElement(); preview.AddToClassList("colour-preview"); parent.Add(preview);
            var row = new VisualElement(); row.AddToClassList("colour-picker-row"); parent.Add(row);
            texture = new Texture2D(Size,Size,TextureFormat.RGBA32,false);
            var wheel = new Image { image = texture }; wheel.AddToClassList("colour-wheel"); row.Add(wheel);
            hueMarker = new VisualElement(); hueMarker.AddToClassList("colour-marker"); wheel.Add(hueMarker);
            squareMarker = new VisualElement(); squareMarker.AddToClassList("colour-marker"); wheel.Add(squareMarker);
            var fields = new VisualElement(); fields.AddToClassList("colour-rgb"); row.Add(fields);
            red = Channel("R",fields); green = Channel("G",fields); blue = Channel("B",fields);
            void FromRgb()
            {
                Color.RGBToHSV(new Color(Mathf.Clamp(red.value,0,255)/255f,Mathf.Clamp(green.value,0,255)/255f,Mathf.Clamp(blue.value,0,255)/255f),out hue,out saturation,out brightness);
                Paint();
            }
            red.RegisterValueChangedCallback(_ => FromRgb()); green.RegisterValueChangedCallback(_ => FromRgb()); blue.RegisterValueChangedCallback(_ => FromRgb());
            void Pick(Vector2 point)
            {
                float x = Mathf.Clamp(point.x,0,Size - 1), y = Size - 1 - Mathf.Clamp(point.y,0,Size - 1);
                if (ringDrag) hue = Mathf.Repeat(.25f - Mathf.Atan2(y - 100,x - 100) / (2 * Mathf.PI),1);
                else { saturation = Mathf.InverseLerp(SquareMin,SquareMax,x); brightness = Mathf.InverseLerp(SquareMin,SquareMax,y); }
                Paint();
            }
            wheel.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                var local = wheel.WorldToLocal(evt.position);
                float radius = (local - new Vector2(100,100)).magnitude;
                bool square = local.x >= SquareMin && local.x <= SquareMax && local.y >= SquareMin && local.y <= SquareMax;
                if (!square && (radius < 80 || radius > 99)) return;
                ringDrag = !square; wheel.CapturePointer(evt.pointerId); Pick(local); evt.StopPropagation();
            });
            wheel.RegisterCallback<PointerMoveEvent>(evt => { if (wheel.HasPointerCapture(evt.pointerId)) { Pick(wheel.WorldToLocal(evt.position)); evt.StopPropagation(); } });
            wheel.RegisterCallback<PointerUpEvent>(evt => { if (wheel.HasPointerCapture(evt.pointerId)) { wheel.ReleasePointer(evt.pointerId); evt.StopPropagation(); } });
            wheel.RegisterCallback<DetachFromPanelEvent>(_ => UnityEngine.Object.Destroy(texture));
            Paint();
        }

        private static IntegerField Channel(string title, VisualElement parent)
        {
            var field = new IntegerField(title); field.AddToClassList("colour-channel"); parent.Add(field); return field;
        }

        private void Paint()
        {
            var pixels = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float radius = new Vector2(x - 100,y - 100).magnitude;
                Color color = Color.clear;
                if (radius >= 80 && radius <= 99) color = Color.HSVToRGB(Mathf.Repeat(.25f - Mathf.Atan2(y - 100,x - 100) / (Mathf.PI * 2),1),1,1);
                if (x >= SquareMin && x <= SquareMax && y >= SquareMin && y <= SquareMax)
                    color = Color.HSVToRGB(hue,Mathf.InverseLerp(SquareMin,SquareMax,x),Mathf.InverseLerp(SquareMin,SquareMax,y));
                pixels[y * Size + x] = color;
            }
            texture.SetPixels32(pixels); texture.Apply();
            var selected = Color.HSVToRGB(hue,saturation,brightness);
            preview.style.backgroundColor = selected;
            red.SetValueWithoutNotify(Mathf.RoundToInt(selected.r * 255)); green.SetValueWithoutNotify(Mathf.RoundToInt(selected.g * 255)); blue.SetValueWithoutNotify(Mathf.RoundToInt(selected.b * 255));
            float angle = (.25f - hue) * Mathf.PI * 2;
            hueMarker.style.left = 100 + Mathf.Cos(angle) * 89 - 5;
            hueMarker.style.top = 99 - Mathf.Sin(angle) * 89 - 5;
            squareMarker.style.left = Mathf.Lerp(SquareMin,SquareMax,saturation) - 5;
            squareMarker.style.top = 199 - Mathf.Lerp(SquareMin,SquareMax,brightness) - 5;
            changed(selected);
        }
    }
}
