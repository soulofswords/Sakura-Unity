using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraSettingsPanel
    {
        private readonly SakuraBackend backend;
        private readonly VisualElement host, body;
        private readonly Label status;
        private readonly Action<string> applyTheme;
        private JObject values = new JObject(), changes = new JObject();
        private readonly List<TextField> secretInputs = new List<TextField>();
        private string selected = "General";
        private bool saving, alive = true;

        public SakuraSettingsPanel(SakuraBackend backend, VisualElement host, Action<string> applyTheme, string initialTab = "General")
        {
            selected = initialTab;
            this.backend = backend; this.applyTheme = applyTheme;
            this.host = new VisualElement();
            this.host.AddToClassList("settings-content");
            host.Add(this.host);
            host = this.host;
            host.RegisterCallback<DetachFromPanelEvent>(evt => { alive = false; ClearSecrets(); });
            var tabs = new ScrollView(ScrollViewMode.Horizontal); tabs.AddToClassList("settings-tabs"); host.Add(tabs);
            tabs.contentContainer.style.flexDirection = FlexDirection.Row;
            foreach (string title in new[] { "Voice", "General", "Models", "Theme", "AI & LLM", "Camera & Audio", "Assistant" })
            { string tab = title; AddButton(title, () => { selected = tab; Render(); }, tabs.contentContainer); }
            body = new VisualElement(); host.Add(body);
            status = Text("Loading your saved settings…", "description", host);
            backend.Api("/api/settings", null, (json, error) =>
            {
                if (!alive) return;
                if (error != null) { status.text = error; return; }
                values = JObject.Parse(json); status.text = ""; Render();
            });
        }

        private void Render()
        {
            ClearSecrets(); body.Clear();
            foreach (var button in host.Q(className: "settings-tabs").Query<Button>().ToList())
                button.EnableInClassList("tab-selected", button.text == selected);
            if (selected == "General")
            {
                var card = Card("Updates & reinstall");
                Check(card, "Keep my settings when reinstalling", "preserve_configuration_on_reinstall");
                Text("Keep your preferences through app updates and reinstalls. Your imported models, voices and artwork are kept.", "description", card);
                card = Card("Assistant identity");
                Field(card, "Assistant name", "assistant_name"); Field(card, "VTuber name", "vtuber_name");
                Select(card, "Avatar presentation", "avatar_presentation", new[] { "auto", "feminine", "masculine", "neutral" }, new[] { "Automatic", "Feminine", "Masculine", "Neutral" });
                Select(card, "How Sakura addresses you", "user_address", new[] { "sir", "maam", "none" }, new[] { "Sir", "Ma’am", "No title" });
                card = Card("Hands-free listening");
                Field(card, "Wake phrase", "wake_word"); Check(card, "Listen for my wake phrase, including in the background", "wake_word_enabled");
                Text("Enable the microphone in Voice & speech. Native listening currently checks the wake phrase after transcription; offline wake detection is still being migrated.", "description", card);
#if UNITY_EDITOR
                var dev = new Foldout { text = "Development backend", value = false }; body.Add(dev);
                var source = new TextField("Preserved source folder") { value = backend.SourceFolder }; dev.Add(source);
                source.RegisterValueChangedCallback(evt => { if (!backend.Connected) backend.SourceFolder = evt.newValue; });
                AddButton("Connect backend", backend.Connect, dev);
#endif
            }
            else if (selected == "Theme")
            {
                var card = Card("Appearance");
                Select(card, "Theme", "ui_theme", new[] { "system", "light", "dark" }, new[] { "System", "Light", "Dark" });
                card = Card("Computer access"); Check(card, "Enable computer access", "computer_control_enabled");
                Select(card, "Access level", "computer_access_level", new[] { "restricted", "full" }, new[] { "Restricted", "Full access" });
                Text("Actions continue through Sakura’s existing approval checks.", "description", card);
                card = Card("About Sakura"); Text("Current version · 2.2", "description", card);
                Text("Unity client migration · " + Application.unityVersion, "description", card);
            }
            else if (selected == "AI & LLM")
            {
                var card = Card("AI provider");
                Select(card, "Provider", "llm_provider", new[] { "ollama", "gemini", "openai", "anthropic", "groq", "openrouter" }, new[] { "Local AI", "Google Gemini", "OpenAI", "Anthropic", "Groq", "OpenRouter" });
                Field(card, "Cloud API key", "api_key", true); Text("Blank keeps your saved key. Keys are stored by the existing encrypted backend.", "description", card);
                Field(card, "Gemini model", "gemini_model"); Field(card, "Cloud model", "cloud_model"); Field(card, "Cloud endpoint", "cloud_endpoint");
                card = Card("Local AI");
                Field(card, "Universal local model", "ollama_model"); Field(card, "Game local model", "game_ollama_model"); Field(card, "Local model folder", "ollama_models_folder");
                Number(card, "Context tokens", "ollama_context", 1024, 8192); Number(card, "Wait for local reply (seconds)", "local_timeout", 5, 120);
                Check(card, "Allow cloud fallback", "gemini_fallback_enabled"); Check(card, "Automatically use cloud under game VRAM pressure", "game_cloud_auto_enabled");
                card = Card("Web search"); Check(card, "Allow web search", "web_search_enabled"); Field(card, "Brave Search API key", "brave_key", true);
            }
            else if (selected == "Voice")
            {
                var card = Card("Voice & speech"); Field(card, "Selected voice ID", "selected_voice");
                Field(card, "Pitch", "tts_pitch"); Field(card, "Rate", "tts_rate");
                card = Card("RVC voice changer"); Field(card, "RVC model", "rvc_model"); Field(card, "RVC index", "rvc_index"); Number(card, "RVC pitch", "rvc_pitch", -24, 24);
                Text("Voice settings and native playback use the original backend. Lip-sync is still being migrated.", "description", card);
            }
            else if (selected == "Models")
            {
                var card = Card("Model preferences");
                Check(card, "Enable physics", "physics_enabled");
                Text("Use Models in the left toolbar for the separate PMX, VRM and Live2D libraries and cloud imports.", "description", card);
            }
            else if (selected == "Camera & Audio")
            {
                var card = Card("NVIDIA tracking setup");
                Field(card, "NVIDIA AR SDK folder", "nvidia_ar_sdk_folder");
                Text("The SDK folder is saved for the existing tracker. Native Unity camera tracking is pending integration.", "description", card);
            }
            else if (selected == "Assistant")
            {
                var card = Card("Conversation mode");
                Select(card, "Mode", "conversation_mode", new[] { "friend", "teacher" }, new[] { "Friend", "Teacher" });
                Select(card, "Reply emotions", "avatar_emotion", new[] { "auto", "off", "happy", "concerned", "amused", "surprised", "focused" }, new[] { "Automatic", "Off", "Happy", "Concerned", "Amused", "Surprised", "Focused" });
                card = Card("Memory & learning"); Check(card, "Long-term memory", "long_term_memory"); Check(card, "Relationship adaptation", "relationship_adaptation");
                Field(card, "News area", "news_area");
            }
            AddButton("Save settings", Save, body);
        }

        private JToken Value(string key) => changes[key] ?? values[key];
        private void Field(VisualElement card, string label, string key, bool secret = false)
        {
            var field = new TextField(label) { value = secret ? "" : (string)Value(key) ?? "", isPasswordField = secret };
            field.name = key; card.Add(field); if (secret) secretInputs.Add(field);
            field.RegisterValueChangedCallback(evt => { if (!secret || !string.IsNullOrWhiteSpace(evt.newValue)) changes[key] = evt.newValue; else changes.Remove(key); });
        }
        private void Number(VisualElement card, string label, string key, int min, int max)
        {
            var field = new IntegerField(label) { value = (int?)Value(key) ?? min }; field.name = key; card.Add(field);
            field.RegisterValueChangedCallback(evt => changes[key] = Math.Max(min, Math.Min(max, evt.newValue)));
        }
        private void Check(VisualElement card, string label, string key)
        {
            var field = new Toggle { text = label, value = (bool?)Value(key) ?? false }; field.name = key; card.Add(field);
            field.RegisterValueChangedCallback(evt => changes[key] = evt.newValue);
        }
        private void Select(VisualElement card, string label, string key, string[] ids, string[] labels)
        {
            int index = Array.IndexOf(ids, (string)Value(key));
            var field = new DropdownField(label, labels.ToList(), Math.Max(0, index)); field.name = key; card.Add(field);
            field.RegisterValueChangedCallback(evt => changes[key] = ids[Array.IndexOf(labels, evt.newValue)]);
        }
        private void Save()
        {
            if (saving || changes.Count == 0) { status.text = saving ? "Saving…" : "No changes to save."; return; }
            saving = true; status.text = "Saving settings…";
            var sent = (JObject)changes.DeepClone();
            backend.Api("/api/settings", sent.ToString(Formatting.None), (json, error) =>
            {
                saving = false; ClearSecrets();
                if (!alive) return;
                if (error != null) { status.text = error; return; }
                var reply = JObject.Parse(json); if (reply["settings"] is JObject settings) values = settings;
                foreach (var property in sent.Properties()) if (JToken.DeepEquals(changes[property.Name], property.Value)) changes.Remove(property.Name);
                if (sent["ui_theme"] != null) applyTheme((string)sent["ui_theme"]);
                status.text = "Settings saved.";
            });
        }
        private void ClearSecrets() { foreach (var input in secretInputs) input.SetValueWithoutNotify(""); secretInputs.Clear(); changes.Remove("api_key"); changes.Remove("brave_key"); }
        private VisualElement Card(string title) { var card = new VisualElement(); card.AddToClassList("card"); body.Add(card); Text(title, "card-title", card); return card; }
        private static Label Text(string text, string css, VisualElement parent) { var label = new Label(text); label.AddToClassList(css); parent.Add(label); return label; }
        private static Button AddButton(string text, Action action, VisualElement parent) { var button = new Button(action) { text = text }; parent.Add(button); return button; }
    }
}
