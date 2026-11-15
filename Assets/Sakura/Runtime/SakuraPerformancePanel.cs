using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    /// <summary>Port of the Python "Expressive companion & pronunciation" card.</summary>
    public sealed class SakuraPerformancePanel
    {
        private readonly SakuraBackend backend;
        private readonly SakuraAvatarViewport viewport;
        private readonly SakuraVoice voice;
        private readonly Foldout root;
        private readonly VisualElement pronunciations;
        private readonly Label status, avatarLabel, bankStatus, voiceStatus;
        private readonly TextField written, spoken;
        private readonly DropdownField language, emotion, voiceChoice, expression, motion, reaction, dialogue, reflection;
        private readonly Toggle performance, banksEnabled, rolesEnabled, reflections;
        private readonly IntegerField weight, rate;
        private readonly FloatField pause;
        private JObject profile;
        private bool alive = true, busy;
        private readonly List<string> emotionIds = new List<string>();
        private readonly List<string> voiceIds = new List<string>();
        private string avatarKey = "";

        public SakuraPerformancePanel(SakuraBackend backend, SakuraAvatarViewport viewport, SakuraVoice voice, VisualElement parent)
        {
            this.backend = backend; this.viewport = viewport; this.voice = voice;
            root = new Foldout { text = "Expressive companion & pronunciation", value = false, name = "companion-performance" };
            root.AddToClassList("companion-performance-foldout"); parent.Add(root);
            var body = new VisualElement(); body.AddToClassList("companion-performance-body"); root.Add(body);
            status = Text("Loading expressive settings…", body, "description"); status.name = "performance-status";

            Text("Pronunciation", body, "section-title");
            Text("Keep the real spelling in chat; change only how speech says a name.", body, "description");
            written = Field("Written name", body); spoken = Field("Say it as", body);
            language = new DropdownField("Language", new List<string> { "All", "English", "Hindi", "Hinglish" }, 0); body.Add(language);
            var wordActions = Row(body);
            wordActions.Add(new Button(AddPronunciation) { text = "Add pronunciation" });
            wordActions.Add(new Button(PreviewPronunciation) { text = "Hear preview" });
            pronunciations = new VisualElement(); body.Add(pronunciations);

            Text("Sentence performance", body, "section-title");
            performance = new Toggle("Match face and voice delivery to each spoken phrase") { value = false }; body.Add(performance);
            Text("Kokoro supports voice and rate changes. Trained emotional acting depends on the voice engine.", body, "description");
            emotion = new DropdownField("Emotion & register", new List<string> { "casual happy" }, 0); body.Add(emotion);
            voiceChoice = new DropdownField("Voice", new List<string> { "Keep my current voice" }, 0); body.Add(voiceChoice);
            rate = new IntegerField("Rate adjustment (%)") { value = 0 }; rate.style.maxWidth = 180; body.Add(rate);
            pause = new FloatField("Pause after phrase (seconds)") { value = .1f }; pause.style.maxWidth = 180; body.Add(pause);
            var performanceActions = Row(body);
            performanceActions.Add(new Button(SaveVoice) { text = "Save emotional voice" });
            voiceStatus = Text("", body, "description"); voiceStatus.name = "performance-voice-status";

            Text("Animation bank for this avatar", body, "section-title");
            avatarLabel = Text(viewport?.LoadedModelPath ?? "Load an avatar, then refresh.", body, "description");
            banksEnabled = new Toggle("Use my per-avatar animation banks") { value = false }; body.Add(banksEnabled);
            expression = new DropdownField("Expression", new List<string> { "No expression" }, 0); body.Add(expression);
            motion = new DropdownField("Motion", new List<string> { "No motion", "groove", "step", "wave", "twist", "disco", "march", "hand_left", "hand_right", "hands_both" }, 0); body.Add(motion);
            weight = new IntegerField("Choice weight") { value = 50 }; weight.style.maxWidth = 160; body.Add(weight);
            var bankActions = Row(body);
            bankActions.Add(new Button(AddBankChoice) { text = "Add choice for selected emotion" });
            bankActions.Add(new Button(ClearBank) { text = "Clear this emotion bank" });
            bankStatus = Text("", body, "description"); bankStatus.name = "performance-bank-status";

            Text("Conversation models", body, "section-title");
            rolesEnabled = new Toggle("Enable optional reaction and dialogue model roles") { value = false }; body.Add(rolesEnabled);
            reflections = new Toggle("Include reflection role") { value = false }; body.Add(reflections);
            reaction = new DropdownField("Reaction model", new List<string> { "Current universal AI" }, 0); body.Add(reaction);
            dialogue = new DropdownField("Dialogue model", new List<string> { "Current universal AI" }, 0); body.Add(dialogue);
            reflection = new DropdownField("Reflection model", new List<string> { "Current universal AI" }, 0); body.Add(reflection);
            var roleActions = Row(body);
            roleActions.Add(new Button(SaveRoles) { text = "Save conversation roles" });
            var refresh = Row(body);
            refresh.Add(new Button(Load) { text = "Refresh models and avatar" });
            emotion.RegisterValueChangedCallback(_ => ShowVoice());

            body.RegisterCallback<DetachFromPanelEvent>(_ => alive = false);
            Load();
        }

        private void Load()
        {
            if (!alive) return;
            root.SetEnabled(false); status.text = "Loading expressive settings…";
            backend.Api("/api/companion/studio", null, (json, error) =>
            {
                if (!alive) return;
                root.SetEnabled(true);
                if (error != null) { status.text = error; return; }
                try
                {
                    profile = JObject.Parse(json);
                    avatarKey = profile.Value<string>("avatar_key") ?? "";
                    avatarLabel.text = profile.Value<string>("avatar") ?? "Load an avatar, then refresh.";
                    FillEmotions(profile["emotions"] as JArray);
                    FillVoices(profile["voices"] as JArray);
                    FillModels(profile["models"] as JArray);
                    FillExpressions();
                    FillPronunciations();
                    performance.SetValueWithoutNotify(profile.Value<bool?>("performance") == true);
                    banksEnabled.SetValueWithoutNotify(profile.Value<bool?>("banks_enabled") == true);
                    var roles = profile["roles"] as JObject;
                    rolesEnabled.SetValueWithoutNotify(roles?.Value<bool?>("enabled") == true);
                    reflections.SetValueWithoutNotify(roles?.Value<bool?>("reflections") == true);
                    SelectModel(reaction, roles?.Value<string>("reaction"));
                    SelectModel(dialogue, roles?.Value<string>("dialogue"));
                    SelectModel(reflection, roles?.Value<string>("reflection"));
                    ShowVoice();
                    status.text = profile.Value<string>("role_status") ?? "";
                }
                catch (Exception failure) { status.text = "Expressive settings could not be read: " + failure.Message; }
            });
        }

        private void FillEmotions(JArray rows)
        {
            emotionIds.Clear();
            if (rows != null) foreach (var row in rows)
            {
                string id = row.Type == JTokenType.String ? row.Value<string>() : row.Value<string>("id");
                if (!string.IsNullOrEmpty(id)) emotionIds.Add(id);
            }
            if (emotionIds.Count == 0) emotionIds.Add("casual_happy");
            emotion.choices = emotionIds.Select(id => id.Replace('_', ' ')).ToList();
            emotion.SetValueWithoutNotify(emotion.choices[0]);
        }

        private void FillVoices(JArray rows)
        {
            voiceIds.Clear(); var labels = new List<string> { "Keep my current voice" };
            if (rows != null) foreach (var row in rows.OfType<JObject>())
            {
                string id = row.Value<string>("id"); if (string.IsNullOrEmpty(id)) continue;
                voiceIds.Add(id); labels.Add((row.Value<string>("name") ?? id) + " · " + (row.Value<string>("language") ?? ""));
            }
            voiceChoice.choices = labels;
        }

        private void FillModels(JArray rows)
        {
            var ids = new List<string>(); var labels = new List<string>();
            if (rows != null) foreach (var row in rows.OfType<JObject>())
            { string id = row.Value<string>("id"); if (string.IsNullOrEmpty(id)) continue; ids.Add(id); labels.Add(row.Value<string>("label") ?? id); }
            if (labels.Count == 0) { ids.Add("current"); labels.Add("Current universal AI"); }
            foreach (var field in new[] { reaction, dialogue, reflection }) { field.choices = labels; field.userData = ids; }
        }

        private void FillExpressions()
        {
            var names = viewport?.Expressions?.Entries.Select(entry => entry.Name).Where(name => !string.IsNullOrEmpty(name)).Distinct().ToList() ?? new List<string>();
            names.Insert(0, "No expression"); expression.choices = names;
        }

        private void FillPronunciations()
        {
            pronunciations.Clear();
            var rows = profile?["pronunciations"] as JArray ?? new JArray();
            for (int index = 0; index < rows.Count; index++)
            {
                int remove = index; var row = rows[index] as JObject; if (row == null) continue;
                var line = Row(pronunciations);
                line.Add(new Label(row.Value<string>("written") + " → " + row.Value<string>("spoken") + " (" + row.Value<string>("language") + ")"));
                line.Add(new Button(() => RemovePronunciation(remove)) { text = "Remove" });
            }
        }

        private string CurrentEmotion()
        {
            int index = Mathf.Clamp(emotion.index, 0, emotionIds.Count - 1);
            return emotionIds[index];
        }

        private void ShowVoice()
        {
            if (profile == null) return;
            string key = CurrentEmotion();
            var map = profile["voice_map"] as JObject;
            var row = map?[key] as JObject;
            int voiceIndex = voiceIds.IndexOf(row?.Value<string>("voice") ?? "") + 1;
            voiceChoice.SetValueWithoutNotify(voiceChoice.choices[Mathf.Clamp(voiceIndex, 0, voiceChoice.choices.Count - 1)]);
            rate.SetValueWithoutNotify(row?.Value<int?>("rate") ?? 0);
            pause.SetValueWithoutNotify(row?.Value<float?>("pause") ?? .1f);
            var bank = (profile["banks"] as JObject)?[avatarKey]?[key] as JArray ?? new JArray();
            bankStatus.text = bank.Count + " choices · " + string.Join(", ", bank.OfType<JObject>().Select(item => item.Value<string>("expression") ?? item.Value<string>("motion")));
        }

        private void AddPronunciation()
        {
            if (profile == null || busy) return;
            string writtenText = (written.value ?? "").Trim(), spokenText = (spoken.value ?? "").Trim();
            if (writtenText.Length == 0 || spokenText.Length == 0) { status.text = "Enter the written name and how to say it."; return; }
            var rows = profile["pronunciations"] as JArray ?? new JArray();
            rows.Add(new JObject { ["written"] = writtenText, ["spoken"] = spokenText, ["language"] = language.value });
            Save(new JObject { ["pronunciations"] = rows }, "Pronunciation saved.");
        }

        private void RemovePronunciation(int index)
        {
            if (profile == null || busy) return;
            var rows = profile["pronunciations"] as JArray ?? new JArray();
            if (index < 0 || index >= rows.Count) return;
            rows.RemoveAt(index); Save(new JObject { ["pronunciations"] = rows }, "Pronunciation removed.");
        }

        private void PreviewPronunciation()
        {
            if (profile == null || busy) return;
            string text = (written.value ?? "").Trim(); if (text.Length == 0) { status.text = "Enter a written name first."; return; }
            backend.Api("/api/companion/studio/pronunciation-preview", new JObject { ["text"] = text, ["language"] = language.value }.ToString(Newtonsoft.Json.Formatting.None), (json, error) =>
            {
                if (!alive) return;
                if (error != null) { status.text = error; return; }
                try
                {
                    string say = JObject.Parse(json).Value<string>("spoken") ?? text;
                    status.text = "Saying: " + say;
                    backend.Api("/api/tts/speak", new JObject { ["text"] = say, ["pronunciation_applied"] = true }.ToString(Newtonsoft.Json.Formatting.None), (audioJson, speechError) =>
                    {
                        if (!alive) return;
                        if (speechError != null) { status.text = speechError; return; }
                        try
                        {
                            string audio = JObject.Parse(audioJson).Value<string>("audio");
                            if (SakuraBackend.ValidAudioPath(audio)) voice?.PlayCohost(audio);
                        }
                        catch (Newtonsoft.Json.JsonException) { status.text = "The voice preview returned an invalid response."; }
                    });
                }
                catch (Newtonsoft.Json.JsonException) { status.text = "The pronunciation preview returned invalid JSON."; }
            });
        }

        private void SaveVoice()
        {
            if (profile == null || busy) return;
            var map = profile["voice_map"] as JObject ?? new JObject();
            string voiceId = voiceChoice.index <= 0 || voiceChoice.index > voiceIds.Count ? "" : voiceIds[voiceChoice.index - 1];
            map[CurrentEmotion()] = new JObject { ["voice"] = voiceId, ["rate"] = Mathf.Clamp(rate.value, -20, 20), ["pause"] = Mathf.Clamp(pause.value, 0f, 1f) };
            Save(new JObject { ["voice_map"] = map, ["performance"] = performance.value }, "Emotional voice saved.");
        }

        private void AddBankChoice()
        {
            if (profile == null || busy) return;
            var banks = profile["banks"] as JObject ?? new JObject();
            var avatar = banks[avatarKey] as JObject ?? new JObject();
            var bank = avatar[CurrentEmotion()] as JArray ?? new JArray();
            string chosenExpression = expression.index <= 0 ? "" : expression.value;
            string chosenMotion = motion.index <= 0 ? "" : motion.value;
            if (string.IsNullOrEmpty(chosenExpression) && string.IsNullOrEmpty(chosenMotion)) { bankStatus.text = "Choose an expression or motion."; return; }
            bank.Add(new JObject { ["expression"] = chosenExpression, ["motion"] = chosenMotion, ["weight"] = Mathf.Clamp(weight.value, 1, 100) });
            avatar[CurrentEmotion()] = bank; banks[avatarKey] = avatar;
            Save(new JObject { ["banks"] = banks, ["banks_enabled"] = banksEnabled.value }, "Animation choice added.");
        }

        private void ClearBank()
        {
            if (profile == null || busy) return;
            var banks = profile["banks"] as JObject ?? new JObject();
            var avatar = banks[avatarKey] as JObject; if (avatar == null) return;
            avatar.Remove(CurrentEmotion()); Save(new JObject { ["banks"] = banks }, "Emotion bank cleared.");
        }

        private void SaveRoles()
        {
            if (profile == null || busy) return;
            var roles = new JObject
            {
                ["enabled"] = rolesEnabled.value, ["reflections"] = reflections.value,
                ["reaction"] = ChosenModel(reaction), ["dialogue"] = ChosenModel(dialogue), ["reflection"] = ChosenModel(reflection)
            };
            Save(new JObject { ["roles"] = roles }, "Conversation roles saved.");
        }

        private void SelectModel(DropdownField field, string id)
        {
            var ids = field?.userData as List<string>; if (ids == null) return;
            int index = ids.IndexOf(id ?? "current"); field.SetValueWithoutNotify(field.choices[Mathf.Clamp(index, 0, field.choices.Count - 1)]);
        }

        private string ChosenModel(DropdownField field)
        {
            var ids = field?.userData as List<string>; return ids == null || ids.Count == 0 ? "current" : ids[Mathf.Clamp(field.index, 0, ids.Count - 1)];
        }

        private void Save(JObject changes, string message)
        {
            if (busy) return; busy = true; root.SetEnabled(false); status.text = "Saving…";
            backend.Api("/api/companion/studio", changes.ToString(Newtonsoft.Json.Formatting.None), (json, error) =>
            {
                if (!alive) return;
                busy = false; root.SetEnabled(true);
                if (error != null) { status.text = error; return; }
                try
                {
                    profile = JObject.Parse(json)["profile"] as JObject ?? profile;
                    FillPronunciations(); ShowVoice(); status.text = message;
                }
                catch (Newtonsoft.Json.JsonException) { status.text = message; }
            });
        }

        private static TextField Field(string label, VisualElement parent)
        {
            var field = new TextField(label); parent.Add(field); return field;
        }
        private static Label Text(string value, VisualElement parent, string css) { var label = new Label(value); label.AddToClassList(css); parent.Add(label); return label; }
        private static VisualElement Row(VisualElement parent) { var row = new VisualElement(); row.AddToClassList("row"); parent.Add(row); return row; }
    }
}
