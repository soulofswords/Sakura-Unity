using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public static class SakuraExtrasPanel
    {
        public static void Build(VisualElement parent, SakuraBackend backend, Func<bool> alive)
        {
            VisualElement Card(string title)
            {
                var box = new VisualElement(); box.AddToClassList("card"); parent.Add(box);
                var label = new Label(title); label.AddToClassList("card-title"); box.Add(label); return box;
            }
            Button Add(VisualElement card, string text, Action action) { var button = new Button(action) { text = text }; card.Add(button); return button; }
            Label Status(VisualElement card) { var label = new Label(); label.style.whiteSpace = WhiteSpace.Normal; card.Add(label); return label; }

            var personaCard = Card("Character cards");
            var personaSelect = new DropdownField("Character role", new List<string> { "Loading…" }, 0); personaCard.Add(personaSelect);
            var personaStatus = Status(personaCard);
            var personaIds = new List<string>();
            void LoadPersonas()
            {
                backend.Api("/api/personas", null, (json, error) =>
                {
                    if (!alive() || error != null) { if (alive() && error != null) personaStatus.text = error; return; }
                    try
                    {
                        var data = JObject.Parse(json); var labels = new List<string>(); personaIds.Clear();
                        foreach (var item in (data["items"] as JArray ?? new JArray()).OfType<JObject>())
                        { personaIds.Add((string)item["id"] ?? ""); labels.Add(((string)item["name"] ?? "Persona") + ((string)item["kind"] == "card" ? " · imported card" : " · shipped")); }
                        personaSelect.choices = labels.Count == 0 ? new List<string> { "No personas available" } : labels;
                        int index = personaIds.IndexOf((string)data["active"] ?? "");
                        if (index >= 0) personaSelect.SetValueWithoutNotify(personaSelect.choices[index]);
                        personaStatus.text = (string)data["note"] ?? (index >= 0 ? "Current role: " + personaSelect.choices[index] : "Choose a character role.");
                    }
                    catch (Exception) { personaStatus.text = "The persona service returned an unreadable response."; }
                });
            }
            personaSelect.RegisterValueChangedCallback(evt =>
            {
                int index = personaSelect.choices.IndexOf(evt.newValue);
                if (index < 0 || index >= personaIds.Count || string.IsNullOrEmpty(personaIds[index])) return;
                backend.Api("/api/personas/activate", new JObject { ["persona_id"] = personaIds[index] }.ToString(), (_, error) => { if (!alive()) return; personaStatus.text = error ?? "Character role updated."; });
            });
            LoadPersonas();

            var toolsCard = Card("PC tools");
            var toolsStatus = Status(toolsCard);
            Add(toolsCard, "System capabilities", () => backend.Api("/api/assistant-tools/capabilities", null, (json, error) =>
            {
                if (!alive()) return;
                if (error != null) { toolsStatus.text = error; return; }
                try
                {
                    var data = JObject.Parse(json);
                    toolsStatus.text = string.Join("\n", (data["capabilities"] as JArray ?? new JArray()).OfType<JObject>().Select(item => (string)item["name"] + ": " + string.Join("; ", (item["examples"] as JArray ?? new JArray()).Values<string>())));
                }
                catch (Exception) { toolsStatus.text = "The tool service returned an unreadable response."; }
            }));
            Add(toolsCard, "Recent tool runs", () => backend.Api("/api/assistant-tools/runs", null, (json, error) =>
            {
                if (!alive()) return;
                if (error != null) { toolsStatus.text = error; return; }
                try
                {
                    var data = JObject.Parse(json); var runs = (data["runs"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                    toolsStatus.text = string.Join("\n", runs.Skip(Math.Max(0, runs.Count - 5)).Select(run => (string)run["status"] + ": " + string.Join(" | ", (run["steps"] as JArray ?? new JArray()).OfType<JObject>().Select(step => (step.Value<bool?>("ok") == true ? "✓ " : "✗ ") + (string)step["request"]))));
                    if (string.IsNullOrWhiteSpace(toolsStatus.text)) toolsStatus.text = "No action sequences in this app session.";
                }
                catch (Exception) { toolsStatus.text = "The tool service returned an unreadable response."; }
            }));

            var archaeologyCard = Card("Saved context");
            var archaeologyQuery = new TextField("What were you looking for?"); archaeologyCard.Add(archaeologyQuery);
            var archaeologyStatus = Status(archaeologyCard);
            Add(archaeologyCard, "Show pending saves", () => backend.Api("/api/memory-archaeology/pending", null, (json, error) =>
            {
                if (!alive()) return;
                if (error != null) { archaeologyStatus.text = error; return; }
                try
                {
                    var data = JObject.Parse(json); var saves = (data["saves"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                    archaeologyStatus.text = saves.Count == 0 ? "Nothing is waiting for context." : string.Join("\n", saves.Select(row => (string)row["topic"] ?? (string)row["url_or_path"] ?? "Saved item"));
                }
                catch (Exception) { archaeologyStatus.text = "Saved context returned an unreadable response."; }
            }));
            Add(archaeologyCard, "Recall saved context", () => backend.Api("/api/memory-archaeology/recall?query=" + Uri.EscapeDataString(archaeologyQuery.value ?? ""), null, (json, error) =>
            {
                if (!alive()) return;
                if (error != null) { archaeologyStatus.text = error; return; }
                try
                {
                    var data = JObject.Parse(json); var lines = new List<string>();
                    foreach (var group in (data["timeline"] as JArray ?? new JArray()).OfType<JObject>())
                        foreach (var row in (group["saves"] as JArray ?? new JArray()).OfType<JObject>())
                            lines.Add(((string)row["topic"] ?? (string)row["url_or_path"] ?? "Saved item") + " — " + ((string)row["context_answer"] ?? "No reason recorded."));
                    archaeologyStatus.text = lines.Count == 0 ? "No matching saved context." : string.Join("\n", lines.Take(12));
                }
                catch (Exception) { archaeologyStatus.text = "Saved context returned an unreadable response."; }
            }));

            var featureCard = Card("Optional features");
            var phoneShare = new Toggle("Share conversation logs with paired phones"); featureCard.Add(phoneShare);
            var cameraStyle = new Toggle("Camera-aware reply delivery"); featureCard.Add(cameraStyle);
            var featureStatus = Status(featureCard);
            backend.Api("/api/features/status", null, (json, error) =>
            {
                if (!alive() || error != null) return;
                try { var data = JObject.Parse(json); phoneShare.SetValueWithoutNotify(data.Value<bool?>("phone_history") == true); cameraStyle.SetValueWithoutNotify(data.Value<bool?>("camera_style") == true); }
                catch (Exception) { }
            });
            phoneShare.RegisterValueChangedCallback(evt => backend.Api("/api/features/phone-sharing", new JObject { ["enabled"] = evt.newValue }.ToString(), (_, error) =>
            {
                if (!alive()) return;
                if (error != null) { phoneShare.SetValueWithoutNotify(!evt.newValue); featureStatus.text = error; }
                else featureStatus.text = evt.newValue ? "Conversation sharing enabled." : "Conversation sharing disabled.";
            }));
            cameraStyle.RegisterValueChangedCallback(evt => backend.Api("/api/features/camera-style", new JObject { ["enabled"] = evt.newValue }.ToString(), (_, error) =>
            {
                if (!alive()) return;
                if (error != null) { cameraStyle.SetValueWithoutNotify(!evt.newValue); featureStatus.text = error; }
                else featureStatus.text = evt.newValue ? "Camera delivery hints enabled." : "Camera delivery hints disabled.";
            }));
        }
    }
}
