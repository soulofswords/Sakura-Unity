using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity.Editor
{
    public sealed class SakuraControlLayoutChecks : EditorWindow
    {
        static SakuraControlLayoutChecks window;
        static double readyAt;
        static int widthIndex;
        static readonly List<string> failures = new List<string>();
        static readonly int[] widths = { 410, 300 };
        const string Receipt = @"C:\AI\Unity-Verification\ui-parity-2026-11-16\control-layout.txt";

        public static void RunBatch()
        {
            failures.Clear(); widthIndex = 0;
            window = CreateInstance<SakuraControlLayoutChecks>();
            window.position = new Rect(0, 0, 500, 900);
            window.Show();
            Build();
            EditorApplication.update += Tick;
        }

        static void Build()
        {
            var root = window.rootVisualElement; root.Clear();
            root.styleSheets.Add(Resources.Load<StyleSheet>("Sakura/Studio"));
            root.AddToClassList("studio");
            var panel = new VisualElement(); panel.AddToClassList("panel"); root.Add(panel);
            panel.style.position = Position.Relative; panel.style.left = 0; panel.style.right = StyleKeyword.Auto;
            panel.style.top = 0; panel.style.bottom = StyleKeyword.Auto;
            panel.style.width = widths[widthIndex]; panel.style.height = 800; panel.style.paddingLeft = 16; panel.style.paddingRight = 16;
            panel.Add(new Toggle { name = "text-toggle", text = "Keep my settings when reinstalling", value = true });
            panel.Add(new Toggle("Spoken stop / wait during playback · use headphones") { name = "label-toggle" });
            panel.Add(new SakuraSlider("Color strength", 0, 100) { name = "number-slider", value = 50, showInputField = true });
            var size = new VisualElement(); size.AddToClassList("size-row"); panel.Add(size);
            var label = new Label("Size"); label.AddToClassList("size-label"); size.Add(label);
            size.Add(new SakuraSlider(10, 300) { name = "size-slider", value = 100 });
            var percent = new Label("100%"); percent.AddToClassList("size-value"); size.Add(percent);
            var fold = new Foldout { text = "Voice adjustment", value = true }; panel.Add(fold);
            fold.Add(new Label("Expanded content"));
            readyAt = EditorApplication.timeSinceStartup + 2;
        }

        static void Tick()
        {
            window.Repaint();
            if (EditorApplication.timeSinceStartup < readyAt) return;
            try
            {
                var root = window.rootVisualElement;
                var toggle = root.Q<Toggle>("text-toggle");
                var text = toggle.Q(className: "unity-toggle__text");
                Require(text != null && text.worldBound.width > 180 && toggle.worldBound.height < 65,
                    "Text-bearing checkbox collapsed: " + text?.worldBound + "; toggle=" + toggle.worldBound);
                var labelled = root.Q<Toggle>("label-toggle");
                Require(labelled.labelElement.worldBound.width > 180 && labelled.worldBound.height < 80,
                    "Constructor-label checkbox collapsed: " + labelled.labelElement.worldBound);
                foreach (var name in new[] { "number-slider", "size-slider" })
                {
                    var slider = root.Q<SakuraSlider>(name);
                    var tracker = slider.Q(className: "unity-base-slider__tracker");
                    var thumb = slider.Q(className: "unity-base-slider__dragger");
                    Require(tracker != null && tracker.worldBound.width >= 120,
                        name + " track is too short: " + tracker?.worldBound);
                    Require(thumb != null && Mathf.Abs(thumb.worldBound.center.y - tracker.worldBound.center.y) < 3,
                        name + " thumb is off track: " + thumb?.worldBound + "; track=" + tracker?.worldBound);
                    Require(tracker.worldBound.height >= 4 && tracker.worldBound.height <= 8,
                        name + " track is distorted: " + tracker.worldBound);
                }
                var numeric = root.Q<SakuraSlider>("number-slider").Q<TextField>();
                Require(numeric != null && numeric.worldBound.width >= 48 && numeric.worldBound.xMax <= widths[widthIndex],
                    "Slider numeric entry is clipped: " + numeric?.worldBound);
                var fold = root.Q<Foldout>();
                Require(fold.Q(className: "unity-foldout__text").worldBound.width > 120,
                    "Foldout label collapsed");
            }
            catch (Exception error) { failures.Add("width=" + widths[widthIndex] + ": " + error); }
            if (++widthIndex < widths.Length) { Build(); return; }
            EditorApplication.update -= Tick;
            Directory.CreateDirectory(Path.GetDirectoryName(Receipt));
            File.WriteAllText(Receipt, failures.Count == 0 ? "PASS: checkbox, foldout and slider layout at 410px and 300px" : string.Join("\n", failures));
            Debug.Log(File.ReadAllText(Receipt));
            window.Close();
            EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
        }

        static void Require(bool condition, string message) { if (!condition) failures.Add("width=" + widths[widthIndex] + ": " + message); }
    }
}
