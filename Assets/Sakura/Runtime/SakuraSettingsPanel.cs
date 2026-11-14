using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        private readonly Dictionary<string, string> voiceLanguages = new Dictionary<string, string>();
        private SakuraSpeakerProfiles speakerProfiles;
        private string selected = "General";
        private bool saving, previewing, alive = true;

        public SakuraSettingsPanel(SakuraBackend backend, VisualElement host, Action<string> applyTheme, string initialTab = "General", bool showTabs = true)
        {
            selected = initialTab;
            this.backend = backend; this.applyTheme = applyTheme;
            this.host = new VisualElement();
            this.host.AddToClassList("settings-content");
            host.Add(this.host);
            host = this.host;
            host.RegisterCallback<DetachFromPanelEvent>(evt => { alive = false; speakerProfiles?.Dispose(); ClearSecrets(); });
            var tabs = new ScrollView(ScrollViewMode.Horizontal); tabs.AddToClassList("settings-tabs"); host.Add(tabs);
            tabs.style.display = showTabs ? DisplayStyle.Flex : DisplayStyle.None;
            tabs.contentContainer.style.flexDirection = FlexDirection.Row;
            string[] pages = backend.Mode == "VTuber"
                ? new[] { "Voice", "General", "Models", "Theme", "Camera & Audio" }
                : new[] { "Voice", "General", "Models", "Theme", "AI & LLM", "Camera & Audio", "Assistant" };
            foreach (string title in pages)
            { string tab = title; AddButton(title, () => { selected = tab; Render(); }, tabs.contentContainer).AddToClassList("settings-tab"); }
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
            speakerProfiles?.Dispose(); speakerProfiles = null;
            ClearSecrets(); body.Clear();
            foreach (var button in host.Q(className: "settings-tabs").Query<Button>().ToList())
                button.EnableInClassList("tab-selected", button.text == selected);
            if (selected == "General")
            {
                var card = Card("Updates & reinstall");
                Check(card, "Keep my settings when reinstalling", "preserve_configuration_on_reinstall");
                Text("Keep your preferences through app updates and reinstalls. Your imported models, voices and artwork are kept.", "description", card);
                card = Card("Encrypted local backup");
                Text("Export characters and saved memories with AES-256-GCM password encryption. API keys, Google/Jio sessions and browser cookies are excluded. Restore creates a new folder and never overwrites your active installation.", "description", card);
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var backupPath = new TextField("Backup file") { value = Path.Combine(documents, "Sakura-Backup.sakurabak") }; card.Add(backupPath);
                var restoreFolder = new TextField("Restore folder") { value = documents }; card.Add(restoreFolder);
                var backupPassword = new TextField("Backup password (at least 12 characters)") { isPasswordField = true }; card.Add(backupPassword);
                var backupStatus = new Label(); backupStatus.style.whiteSpace = WhiteSpace.Normal; card.Add(backupStatus);
                void RunBackup(string operation)
                {
                    string path = (backupPath.value ?? "").Trim().Trim('"');
                    string secret = backupPassword.value ?? "";
                    if (secret.Length < 12) { backupStatus.text = "Use a backup password of at least 12 characters."; return; }
                    if (operation == "create" && !path.EndsWith(".sakurabak", StringComparison.OrdinalIgnoreCase)) path += ".sakurabak";
                    backupPassword.value = "";
                    backupStatus.text = operation == "create" ? "Encrypting the model library and saved memories…" : "Decrypting into a new folder…";
                    JObject body = operation == "create"
                        ? new JObject { ["path"] = path, ["password"] = secret }
                        : new JObject { ["backup"] = path, ["parent"] = (restoreFolder.value ?? "").Trim().Trim('"'), ["password"] = secret };
                    backend.Api("/api/backup/" + operation, body.ToString(), (json, error) =>
                    {
                        if (!alive) return;
                        if (error != null) { backupStatus.text = error; return; }
                        try
                        {
                            var result = JObject.Parse(json);
                            backupStatus.text = (string)result["message"] ?? ("Backup ready · " + ((string)result["path"] ?? path));
                        }
                        catch (Exception) { backupStatus.text = "The backup service returned an unreadable response."; }
                    });
                }
                AddButton("Create backup", () => RunBackup("create"), card);
                AddButton("Restore to folder", () => RunBackup("restore"), card);
                Text("Keep the password safe. Sakura cannot recover it.", "description", card);
                var extras = new Foldout { text = "More Sakura features", value = false }; body.Add(extras);
                SakuraExtrasPanel.Build(extras, backend, () => alive);
                card = Card("Assistant identity");
                Field(card, "Assistant name", "assistant_name"); Field(card, "VTuber name", "vtuber_name");
                Select(card, "Avatar presentation", "avatar_presentation", new[] { "auto", "feminine", "masculine", "neutral" }, new[] { "Automatic", "Feminine", "Masculine", "Neutral" });
                Select(card, "How Sakura addresses you", "user_address", new[] { "sir", "maam", "none" }, new[] { "Sir", "Ma’am", "No title" });
                card = Card("Hands-free listening");
                Field(card, "Wake phrase", "wake_word"); Check(card, "Listen for my wake phrase, including in the background", "wake_word_enabled");
                Text("Enable the microphone in Voice & speech. Install the optional English offline wake pack to reject speech without normal transcription until your wake phrase is heard. Without it, the transcript wake gate remains active.", "description", card);
                var wakeStatus = new Label(); card.Add(wakeStatus);
                void WakeAction(string action) => backend.Api("/api/unity/wake/" + action, "{}", (json, error) => {
                    if (!alive) return;
                    if (error != null) { wakeStatus.text = error; return; }
                    var result = JObject.Parse(json);
                    wakeStatus.text = result.Value<bool>("installed") ? "Offline wake pack ready." : "Offline wake pack is not installed.";
                });
                AddButton("Install offline wake pack", () => { wakeStatus.text = "Downloading offline wake pack…"; WakeAction("install"); }, card);
                AddButton("Check offline wake pack", () => WakeAction("state"), card);
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
                string[] themeIds = { "system", "light", "dark" };
                var theme = new DropdownField("Theme", new List<string> { "System", "Light", "Dark" }, Math.Max(0, Array.IndexOf(themeIds, (string)Value("ui_theme")))); card.Add(theme);
                theme.name = "ui_theme";
                // The web app persists the appearance choice immediately so the native startup dialog can read it.
                theme.RegisterValueChangedCallback(evt =>
                {
                    string choice = themeIds[theme.index];
                    applyTheme(choice);
                    backend.Api("/api/settings", new JObject { ["ui_theme"] = choice }.ToString(), (json, error) =>
                    {
                        if (!alive) return;
                        if (error != null) { status.text = error; return; }
                        values["ui_theme"] = choice; changes.Remove("ui_theme");
                    });
                });
                Text("System follows your device’s light or dark appearance.", "description", card);
                card = Card("About Sakura");
                var version = Text("Reading installed version…", "description", card);
                backend.Api("/api/version", null, (json, error) =>
                {
                    if (!alive || version.panel == null) return;
                    if (error != null) { version.text = "Version unavailable. Restart Sakura and try again."; return; }
                    try
                    {
                        var data = JObject.Parse(json);
                        version.text = (string)data["product"] + " · Version " + (string)data["version"];
                    }
                    catch (JsonException) { version.text = "Version unavailable. Restart Sakura and try again."; }
                });
                AddButton("Sakura website: guides, features and setup", () => Application.OpenURL("https://soulofswords.github.io/Sakura-website/"), card);
                AddButton("Release history and downloads", () => Application.OpenURL("https://github.com/soulofswords/Sakura-Downloads/releases"), card);
                card = Card("Graphics");
                var output = backend.GetComponent<SakuraOutput>();
                if (output != null)
                {
                    var quality = new DropdownField("Rendering quality", new List<string> { "Low · lower GPU usage", "Balanced", "High · sharper output" }, (int)output.Preset); card.Add(quality);
                    quality.RegisterValueChangedCallback(evt =>
                    {
                        output.SetQuality((SakuraOutput.Quality)quality.index);
                        quality.SetValueWithoutNotify(output.Preset == SakuraOutput.Quality.Low ? "Low · lower GPU usage" : output.Preset == SakuraOutput.Quality.High ? "High · sharper output" : "Balanced");
                    });
                }
                Text("Controls character and stage rendering quality. Higher quality uses more GPU memory. Changes save automatically.", "description", card);
                if (backend.Mode != "VTuber")
                {
                    card = Card("Computer access"); Check(card, "Enable computer access", "computer_control_enabled");
                    Select(card, "Access level", "computer_access_level", new[] { "restricted", "full" }, new[] { "Restricted", "Full access" });
                    Text("Actions continue through Sakura’s existing approval checks.", "description", card);
                }
            }
            else if (selected == "AI & LLM")
            {
                var card = Card("AI provider");
                Select(card, "Provider", "llm_provider", new[] { "ollama", "auto", "gemini", "openai", "anthropic", "groq", "openrouter", "xai", "deepseek", "compatible" }, new[] { "Local AI", "Auto-detect API key", "Google Gemini", "OpenAI", "Anthropic", "Groq", "OpenRouter", "xAI", "DeepSeek", "Custom OpenAI-compatible API" });
                Field(card, "Cloud API key", "api_key", true); Text("Blank keeps your saved key. Keys are stored by the existing encrypted backend.", "description", card);
                Field(card, "Gemini model", "gemini_model"); Field(card, "Cloud model", "cloud_model"); Field(card, "Cloud endpoint", "cloud_endpoint");
                AddCloudModelDiscovery(card);
                AddAiConnectionTest(card);
                card = Card("Local AI");
                Field(card, "Universal local model", "ollama_model"); Field(card, "Game local model", "game_ollama_model"); Field(card, "Local model folder", "ollama_models_folder");
                AddLocalModelDiscovery(card);
                Number(card, "Context tokens", "ollama_context", 1024, 8192); Number(card, "Wait for local reply (seconds)", "local_timeout", 5, 120);
                Check(card, "Allow cloud fallback", "gemini_fallback_enabled"); Check(card, "Automatically use cloud under game VRAM pressure", "game_cloud_auto_enabled");
                card = Card("Web search"); Check(card, "Allow web search", "web_search_enabled"); Field(card, "Brave Search API key", "brave_key", true);
            }
            else if (selected == "Voice")
            {
                var card = Card("Voice & speech");
                if (backend.Mode != "VTuber")
                {
                Text("Conversation mode", "section-title",card);
                var modes = new VisualElement(); modes.AddToClassList("row"); card.Add(modes);
                foreach (string choice in new[] { "friend","teacher" })
                {
                    string mode = choice;
                    bool active = ((string)Value("conversation_mode") ?? "friend") == mode;
                    var toggle = AddButton("",() =>
                    {
                        backend.Api("/api/settings",new JObject { ["conversation_mode"] = mode }.ToString(),(_,error) =>
                        {
                            if (!alive) return;
                            if (error != null) { status.text = error; return; }
                            values["conversation_mode"] = mode; changes.Remove("conversation_mode");
                            if (selected == "Voice") Render();
                        });
                    },modes);
                    toggle.AddToClassList("conversation-switch");
                    toggle.name = "conversation-" + mode;
                    toggle.tooltip = char.ToUpperInvariant(mode[0]) + mode.Substring(1) + (active ? " On" : " Off");
                    toggle.Add(new Label(char.ToUpperInvariant(mode[0]) + mode.Substring(1)));
                    var track = new VisualElement(); track.AddToClassList("switch-track"); toggle.Add(track);
                    var thumb = new VisualElement(); thumb.AddToClassList("switch-thumb"); track.Add(thumb);
                    toggle.Add(new Label(active ? "On" : "Off"));
                    toggle.EnableInClassList("mode-active",active);
                }
                Text(((string)Value("conversation_mode") ?? "friend") == "friend" ? "Friend · warm, casual conversation." : "Teacher · patient explanations and step-by-step guidance.","description",card);
                Select(card,"Reply emotions","avatar_emotion",new[] { "auto","off","happy","concerned","amused","surprised","focused" },new[] { "Automatic · match the reply","Off · neutral","Happy","Concerned","Amused","Surprised","Focused" });
                }
                var voices = new DropdownField("Voice",new List<string> { (string)Value("selected_voice") ?? "Loading voices…" },0); card.Add(voices);
                voices.name = "voice-preset";
                var language = new DropdownField("Voice & reply language", new List<string> { "English", "Hindi", "Hinglish" }, 0); language.name = "voice-language"; language.SetEnabled(false); card.Add(language);
                Text("Language follows the selected voice. Hindi and Hinglish also use Hindi speech recognition. Save settings to apply your voice and language together.", "description", card);
                var library = new Foldout { text = "Voice library", value = false }; card.Add(library);
                var libraryContents = new VisualElement(); library.Add(libraryContents);
                backend.Api("/api/voices",null,(json,error) =>
                {
                    if (!alive || selected != "Voice" || voices.panel == null) return;
                    if (error != null) { status.text = error; return; }
                    var result = JObject.Parse(json);
                    var options = (result["presets"] as JArray ?? new JArray()).Concat(result["rvc_models"] as JArray ?? new JArray()).ToArray();
                    var ids = options.Select(row => (string)row["id"]).ToList();
                    if (ids.Count == 0) return;
                    voiceLanguages.Clear();
                    foreach (var row in options) voiceLanguages[(string)row["id"]] = (string)row["language"] ?? "English";
                    language.choices = new[] { "English", "Hindi", "Hinglish" }.Concat(voiceLanguages.Values).Distinct().ToList();
                    string currentId = (string)Value("selected_voice");
                    language.SetValueWithoutNotify(currentId != null && voiceLanguages.TryGetValue(currentId, out string currentLanguage) ? currentLanguage : "English");
                    language.SetEnabled(true);
                    var rememberedVoices = new Dictionary<string, string>();
                    if (currentId != null) rememberedVoices[language.value] = currentId;
                    voices.choices = options.Select(row =>
                    {
                        string title = (string)row["name"] ?? (string)row["id"];
                        return row["backend"] == null ? title : string.Join(" · ",new[] { (string)row["backend"],(string)row["gender"],(string)row["language"],title }.Where(value => !string.IsNullOrEmpty(value)));
                    }).ToList();
                    voices.index = Math.Max(0,ids.IndexOf((string)Value("selected_voice")));
                    voices.RegisterValueChangedCallback(_ =>
                    {
                        if (voices.index < 0 || voices.index >= ids.Count) return;
                        changes["selected_voice"] = ids[voices.index];
                        var chosen = options[voices.index];
                        string chosenLanguage = (string)chosen["language"] ?? "English";
                        language.SetValueWithoutNotify(chosenLanguage); rememberedVoices[chosenLanguage] = ids[voices.index];
                        if (chosen["pth"] != null)
                        {
                            changes["rvc_model"] = (string)chosen["pth"];
                            changes["rvc_index"] = (string)chosen["index"] ?? "";
                        }
                    });
                    language.RegisterValueChangedCallback(evt =>
                    {
                        string currentVoice = (string)Value("selected_voice");
                        var playingVoice = backend.GetComponent<SakuraVoice>();
                        if (backend.Busy || playingVoice?.Speaking == true)
                        {
                            language.SetValueWithoutNotify(currentVoice != null && voiceLanguages.TryGetValue(currentVoice, out string previousLanguage) ? previousLanguage : "English");
                            status.text = "Wait for the current reply to finish before changing voice or language."; return;
                        }
                        if (currentVoice != null && voiceLanguages.TryGetValue(currentVoice, out string selectedLanguage) && selectedLanguage == evt.newValue) return;
                        string preferred = rememberedVoices.TryGetValue(evt.newValue, out string remembered) ? remembered : null;
                        int choice = preferred == null ? -1 : ids.IndexOf(preferred);
                        if (choice < 0)
                        {
                            string gender = (string)options.FirstOrDefault(row => (string)row["id"] == currentVoice)?["gender"];
                            var candidates = options.Where(row => ((string)row["language"] ?? "English") == evt.newValue).ToList();
                            var target = candidates.FirstOrDefault(row => (string)row["gender"] == gender && (string)row["backend"] == "kokoro") ?? candidates.FirstOrDefault();
                            choice = target == null ? -1 : ids.IndexOf((string)target["id"]);
                        }
                        if (choice >= 0) { voices.index = choice; status.text = evt.newValue + " voice selected. Save settings to apply."; }
                        else { status.text = "No available voice was found for " + evt.newValue + "."; }
                    });
                    RenderVoiceLibrary(libraryContents, options, voices, ids);
                });
                AddButton("Preview voice", PreviewVoice, card);
                AddButton("Stop speaking", () => backend.GetComponent<SakuraVoice>()?.StopSpeaking(), card);
                if (backend.Mode == "Companion" && backend.GetComponent<SakuraVoice>() is SakuraVoice interruptVoice)
                {
                    var interruption = new Toggle("Spoken stop / wait during playback · use headphones") { value = interruptVoice.SpokenInterruptionEnabled }; card.Add(interruption);
                    interruption.RegisterValueChangedCallback(evt => interruptVoice.SpokenInterruptionEnabled = evt.newValue);
                    Text("Enable the microphone first. This session-only option listens for stop/wait commands during playback. Native microphone capture has no acoustic echo cancellation. Headphones keep Sakura’s voice out of the microphone.", "description", card);
                }
                var advanced = new Foldout { text = "Voice adjustment", value = false }; card.Add(advanced);
                Field(advanced, "Pitch", "tts_pitch"); Field(advanced, "Rate", "tts_rate");
                card = Card("RVC voice changer"); Field(card, "RVC model", "rvc_model"); Field(card, "RVC index", "rvc_index"); Number(card, "RVC pitch", "rvc_pitch", -24, 24);
                var indexRate = new Slider("RVC index strength", 0, 1) { value = (float?)Value("rvc_index_rate") ?? .75f }; card.Add(indexRate);
                indexRate.RegisterValueChangedCallback(evt => changes["rvc_index_rate"] = evt.newValue);
                Text("Choose an installed RVC voice in the Voice library or enter model and index paths from the Sakura voices folder.", "description", card);
                if (backend.Mode == "VTuber")
                {
                    var changer = backend.GetComponent<SakuraVoiceChanger>() ?? backend.gameObject.AddComponent<SakuraVoiceChanger>();
                    changer.BuildPanel(card);
                }
                var import = new Foldout { text = "Import custom RVC voice", value = false }; card.Add(import);
                var voiceName = new TextField("Voice name") { maxLength = 48 }; import.Add(voiceName);
                var modelPath = new TextField(".pth file on this PC"); import.Add(modelPath);
                var indexPath = new TextField(".index file (optional)"); import.Add(indexPath);
                Text("Import copies these files into Sakura’s voice library and activates the voice.", "description", import);
                Button importButton = null;
                importButton = AddButton("Import and activate voice", () =>
                {
                    var nativeVoice = backend.GetComponent<SakuraVoice>();
                    if (backend.Busy || (nativeVoice != null && nativeVoice.Speaking)) { status.text = "Wait for the current reply to finish before importing a voice."; return; }
                    string importedName = voiceName.value;
                    backend.GetComponent<SakuraVoiceChanger>()?.StopConversion();
                    importButton.SetEnabled(false); status.text = "Importing RVC voice…";
                    backend.UploadRvcVoice(importedName, modelPath.value, indexPath.value, (json, error) =>
                    {
                        if (!alive) return;
                        importButton.SetEnabled(true);
                        if (error != null) { status.text = error; return; }
                        try
                        {
                            var result = JObject.Parse(json);
                            values["selected_voice"] = "rvc_" + importedName.Trim().ToLowerInvariant().Replace(' ', '_');
                            values["rvc_model"] = result["pth"]; values["rvc_index"] = result["index"] ?? "";
                            changes.Remove("selected_voice"); changes.Remove("rvc_model"); changes.Remove("rvc_index");
                            status.text = "Voice imported and activated.";
                            if (selected == "Voice") Render();
                        }
                        catch (JsonException) { status.text = "The voice import returned an invalid response. Refresh the Voice page to check the library."; }
                    });
                }, import);
                if (backend.Mode == "Companion") speakerProfiles = new SakuraSpeakerProfiles(backend, body);
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
                Text("Save the SDK folder before starting the native RTX tracker. Webcam CPU tracking and connected phone tracking are available below.", "description", card);
                var tracking = backend.GetComponent<SakuraTracking>();
                if (tracking != null) new SakuraTrackingPanel(tracking, body);
                else Text("Camera tracking is unavailable in this session.", "description", card);
                card = Card("Speech input");
                var voice = backend.GetComponent<SakuraVoice>();
                if (voice != null)
                {
                    var devices = new List<string> { "System default" }; devices.AddRange(Microphone.devices);
                    var microphone = new DropdownField("Microphone", devices, Math.Max(0, devices.IndexOf(voice.MicrophoneDevice))); card.Add(microphone);
                    microphone.RegisterValueChangedCallback(_ =>
                    {
                        bool listening = voice.Listening;
                        voice.StopListening(); voice.MicrophoneDevice = microphone.index == 0 ? "" : microphone.value;
                        if (listening) voice.StartListening();
                    });
                    AddButton("Stop microphone", voice.StopListening, card);
                    Text("Microphone starts off. Use the chat microphone to enable listening. Spoken stop/wait checks run only after you enable listening.", "description", card);
                }
                card = Card("Screen audio listening");
                var screenAudio = new Toggle("Listen to PC audio while screen sharing") { value = false }; card.Add(screenAudio);
                var screenAudioStatus = new Label("Screen audio is optional and only runs while screen sharing is active."); screenAudioStatus.style.whiteSpace = WhiteSpace.Normal; card.Add(screenAudioStatus);
                backend.Api("/api/screen-audio/sources", null, (json, error) =>
                {
                    if (!alive) return;
                    if (error != null) { screenAudioStatus.text = "Audio sources unavailable: " + error; screenAudio.SetEnabled(false); return; }
                    try
                    {
                        var sources = (JObject.Parse(json)["sources"] as JArray ?? new JArray()).OfType<JObject>().Select(item => (string)item["name"]).Where(name => !string.IsNullOrEmpty(name)).ToList();
                        screenAudioStatus.text = sources.Count == 0 ? "No audio sources were reported." : "Available: " + string.Join(", ", sources.Take(6)) + ".";
                    }
                    catch (Exception) { screenAudioStatus.text = "The audio service returned an unreadable response."; }
                });
                screenAudio.RegisterValueChangedCallback(evt => backend.Api("/api/screen-audio", new JObject { ["enabled"] = evt.newValue, ["sources"] = new JArray("default-output"), ["computer_only"] = false, ["paused"] = false }.ToString(), (json, error) =>
                {
                    if (!alive) return;
                    if (error != null) { screenAudio.SetValueWithoutNotify(!evt.newValue); screenAudioStatus.text = error; return; }
                    try { var data = JObject.Parse(json); screenAudioStatus.text = (string)data["message"] ?? (evt.newValue ? "Screen audio listening on." : "Screen audio listening off."); }
                    catch (Exception) { screenAudioStatus.text = evt.newValue ? "Screen audio listening on." : "Screen audio listening off."; }
                }));
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

        private void SetModel(string key, string value)
        {
            changes[key] = value;
            body.Q<TextField>(key)?.SetValueWithoutNotify(value);
            status.text = "Selected " + value + ". Save settings to activate it.";
        }

        private void AddLocalModelDiscovery(VisualElement parent)
        {
            var choices = new VisualElement(); parent.Add(choices);
            Button scan = null;
            Action refresh = () =>
            {
                scan.SetEnabled(false); status.text = "Reading local models…";
                backend.Api("/api/ollama/models", new JObject { ["folder"] = (string)Value("ollama_models_folder") ?? "" }.ToString(Formatting.None), (json, error) =>
                {
                    if (!alive || choices.panel == null) return;
                    scan.SetEnabled(true);
                    if (error != null) { status.text = error; return; }
                    try
                    {
                        var data = JObject.Parse(json);
                        choices.Clear();
                        var ollama = (data["models"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                        var native = (data["native_models"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                        var localIds = ollama.Select(row => (string)row["name"]).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
                        var universalIds = localIds.Concat(native.Select(row => (string)row["name"])).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
                        var nativeLabels = native.Where(row => row["name"] != null).GroupBy(row => (string)row["name"]).ToDictionary(group => group.Key, group => (string)group.First()["label"] + " · " + (string)group.First()["engine"]);
                        ModelChoices(choices, "Available universal models", "ollama_model", universalIds, nativeLabels);
                        ModelChoices(choices, "Available game models", "game_ollama_model", localIds, null);
                        var qwen = native.FirstOrDefault(row => (string)row["engine"] == "colibri" && System.Text.RegularExpressions.Regex.IsMatch((string)row["label"] ?? "", @"qwen.*3[.]6", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                        if (qwen != null) AddButton("Use " + ((string)qwen["label"] ?? "Qwen3.6"), () => SetModel("ollama_model", (string)qwen["name"]), choices);
                        Text("Universal models include installed native and GGUF engines. Game models require vision support for screen understanding.", "description", choices);
                        status.text = (string)data["warning"] ?? (universalIds.Count + " local models available.");
                    }
                    catch (JsonException) { status.text = "Local model discovery returned an invalid response."; }
                });
            };
            scan = AddButton("Find installed local models", refresh, parent);
            // Only reads the existing catalog. Selecting and saving remain explicit actions.
            parent.schedule.Execute(refresh).ExecuteLater(1);
        }

        private void ModelChoices(VisualElement parent, string label, string key, List<string> ids, Dictionary<string, string> labels)
        {
            string saved = (string)Value(key) ?? "";
            ids = new List<string>(ids);
            if (!ids.Contains(saved)) ids.Insert(0, saved);
            var titles = ids.Select(id => id == "" ? "Use default" : labels != null && labels.TryGetValue(id, out string title) ? title : id).ToList();
            var field = new DropdownField(label, titles, Math.Max(0, ids.IndexOf(saved))); parent.Add(field);
            field.RegisterValueChangedCallback(_ => { if (field.index >= 0 && field.index < ids.Count) SetModel(key, ids[field.index]); });
        }

        private void AddCloudModelDiscovery(VisualElement parent)
        {
            var choices = new VisualElement(); parent.Add(choices);
            parent.Q<DropdownField>("llm_provider")?.RegisterValueChangedCallback(_ => choices.Clear());
            Button discover = null;
            discover = AddButton("Find provider models", () =>
            {
                string provider = (string)Value("llm_provider") ?? "ollama";
                if (provider == "ollama") { status.text = "Choose a cloud provider to find cloud models."; return; }
                discover.SetEnabled(false); status.text = "Finding provider models…";
                var payload = new JObject
                {
                    ["provider"] = provider, ["api_key"] = (string)changes["api_key"] ?? "",
                    ["model"] = (string)Value(provider == "gemini" ? "gemini_model" : "cloud_model") ?? "",
                    ["endpoint"] = (string)Value("cloud_endpoint") ?? ""
                };
                backend.Api("/api/llm/models", payload.ToString(Formatting.None), (json, error) =>
                {
                    if (!alive || choices.panel == null) return;
                    discover.SetEnabled(true);
                    if (error != null) { status.text = error; return; }
                    if (provider != (string)Value("llm_provider")) { status.text = "Provider changed. Find models for the selected provider."; return; }
                    try
                    {
                        var data = JObject.Parse(json);
                        choices.Clear();
                        string discoveredProvider = (string)data["provider"] ?? provider;
                        var ids = (data["models"] as JArray ?? new JArray()).Values<string>().Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
                        ModelChoices(choices, "Available provider models", discoveredProvider == "gemini" ? "gemini_model" : "cloud_model", ids, null);
                        status.text = ids.Count + " models found. Save settings to use your selection.";
                    }
                    catch (JsonException) { status.text = "Provider model discovery returned an invalid response."; }
                });
            }, parent);
        }

        private void AddAiConnectionTest(VisualElement parent)
        {
            Text("Connection testing uses your saved AI settings. Save changes before testing a new provider, model or key.", "description", parent);
            Button test = null;
            test = AddButton("Test saved AI connection", () =>
            {
                if (backend.Busy || saving) { status.text = "Wait for the current reply or settings save to finish before testing."; return; }
                string mode = backend.Mode;
                test.SetEnabled(false); status.text = "Starting services to test your saved AI connection…";
                backend.Api("/api/unity/start-services", "{}", (_, startupError) =>
                {
                    if (!alive || backend.Mode != mode || test.panel == null) return;
                    if (startupError != null) { test.SetEnabled(true); status.text = startupError; return; }
                    status.text = "Testing your saved AI connection…";
                    backend.Api("/api/llm/test", "{}", (json, error) =>
                    {
                        if (!alive || backend.Mode != mode || test.panel == null) return;
                        test.SetEnabled(true);
                        if (error != null) { status.text = error; return; }
                        try
                        {
                            var result = JObject.Parse(json);
                            status.text = (string)result["message"] ?? (result.Value<bool?>("ok") == true ? "AI connection is ready." : "AI connection could not be verified.");
                        }
                        catch (JsonException) { status.text = "AI connection testing returned an invalid response."; }
                    });
                });
            }, parent);
        }

        private void RenderVoiceLibrary(VisualElement parent, JToken[] options, DropdownField voices, List<string> ids)
        {
            parent.Clear();
            foreach (var group in options.GroupBy(row => row["backend"] == null ? "Custom RVC" :
                string.Join(" · ", new[] { (string)row["backend"], (string)row["gender"], (string)row["language"] }.Where(part => !string.IsNullOrEmpty(part)))))
            {
                Text(group.Key, "section-title", parent);
                foreach (var row in group)
                {
                    string id = (string)row["id"];
                    var entry = new VisualElement(); entry.AddToClassList("card"); parent.Add(entry);
                    Text((string)row["name"] ?? id, "card-title", entry);
                    string description = (string)row["desc"] ?? (string)row["character"];
                    if (!string.IsNullOrEmpty(description)) Text(description, "description", entry);
                    AddButton("Use voice", () =>
                    {
                        changes["selected_voice"] = id; voices.index = ids.IndexOf(id);
                        if (row["pth"] != null)
                        {
                            changes["rvc_model"] = (string)row["pth"];
                            changes["rvc_index"] = (string)row["index"] ?? "";
                        }
                        status.text = ((string)row["name"] ?? id) + " selected. Save settings to apply.";
                    }, entry);
                }
            }
        }

        private void PreviewVoice()
        {
            var voice = backend.GetComponent<SakuraVoice>();
            if (voice == null) { status.text = "Native voice playback is unavailable."; return; }
            if (previewing || backend.Busy || voice.Speaking) { status.text = "Wait for the current reply or preview to finish before previewing a voice."; return; }
            previewing = true;
            status.text = "Preparing voice preview…";
            string mode = backend.Mode;
            backend.Api("/api/unity/start-services", "{}", (_, serviceError) =>
            {
                if (!alive || backend.Mode != mode) { previewing = false; return; }
                if (serviceError != null) { previewing = false; status.text = serviceError; return; }
                bool customVoice = ((string)Value("selected_voice") ?? "").StartsWith("rvc_", StringComparison.Ordinal);
                string previewVoiceId = (string)Value("selected_voice") ?? "kokoro_af_heart";
                string previewLanguage = voiceLanguages.TryGetValue(previewVoiceId, out string knownLanguage) ? knownLanguage : "English";
                var payload = new JObject
                {
                    ["text"] = previewLanguage == "Hindi" ? "नमस्ते! मेरी आवाज़ ऐसी है। क्या आपको पसंद आई?" : previewLanguage == "Hinglish" ? "Hello! Meri voice aisi hai. Aapko kaisi lagi?" : "Hello! This is how I sound. Do you like this voice?",
                    ["voice"] = (string)Value("selected_voice") ?? "kokoro_af_heart",
                    ["pitch"] = (string)Value("tts_pitch") ?? "+0Hz",
                    ["rate"] = (string)Value("tts_rate") ?? "+0%",
                    ["rvc_model"] = customVoice ? (string)Value("rvc_model") ?? "" : "",
                    ["rvc_index"] = customVoice ? (string)Value("rvc_index") ?? "" : "",
                    ["rvc_pitch"] = (int?)Value("rvc_pitch") ?? 0,
                    ["rvc_index_rate"] = (float?)Value("rvc_index_rate") ?? .75f
                };
                backend.Api("/api/tts/test", payload.ToString(Formatting.None), (json, error) =>
                {
                    previewing = false;
                    if (!alive || backend.Mode != mode) return;
                    if (error != null) { status.text = error; return; }
                    try
                    {
                        var reply = JObject.Parse(json);
                        string audio = (string)reply["audio"];
                        if (!SakuraBackend.ValidAudioPath(audio)) { status.text = "The preview returned an invalid audio path."; return; }
                        if (mode == "VTuber") voice.PlayReadout(audio); else voice.PlayCohost(audio);
                        status.text = (string)reply["voice_notice"] ?? "Playing voice preview.";
                    }
                    catch (JsonException) { status.text = "The preview returned an invalid response."; }
                });
            });
        }
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
            if (changes["selected_voice"] != null || changes["rvc_model"] != null || changes["rvc_index"] != null || changes["rvc_pitch"] != null || changes["rvc_index_rate"] != null)
                backend.GetComponent<SakuraVoiceChanger>()?.StopConversion();
            saving = true; status.text = "Saving settings…";
            var sent = (JObject)changes.DeepClone();
            backend.Api("/api/settings", sent.ToString(Formatting.None), (json, error) =>
            {
                saving = false; ClearSecrets();
                if (error == null && (sent["wake_word"] != null || sent["wake_word_enabled"] != null)) backend.GetComponent<SakuraVoice>()?.RefreshSettings();
                if (error == null && sent["physics_enabled"] != null) backend.GetComponent<SakuraStudio>()?.RefreshModelPhysics();
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
