using System;
using System.Collections.Generic;
using System.IO;
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
            var personaFile = new TextField("Card file (.json or .png)") { value = "" }; personaCard.Add(personaFile);
            Add(personaCard, "Import character card", () =>
            {
                string path = (personaFile.value ?? "").Trim().Trim('"');
                if (!File.Exists(path)) { personaStatus.text = "Choose an existing character card file."; return; }
                try
                {
                    var info = new FileInfo(path);
                    if (info.Length <= 0 || info.Length > 32 * 1024 * 1024) { personaStatus.text = "Character cards must be between 1 byte and 32 MB."; return; }
                    var payload = new JObject { ["file_name"] = Path.GetFileName(path), ["data"] = Convert.ToBase64String(File.ReadAllBytes(path)) };
                    personaStatus.text = "Importing card…";
                    backend.Api("/api/personas/import", payload.ToString(), (json, error) =>
                    {
                        if (!alive()) return;
                        if (error != null) { personaStatus.text = error; return; }
                        try
                        {
                            var data = JObject.Parse(json);
                            if (data.Value<bool?>("ok") == false) { personaStatus.text = (string)data["error"] ?? "The card was refused."; return; }
                            personaStatus.text = "Imported " + ((string)data["name"] ?? "character card") + ". It is available in the role list but not active yet.";
                            LoadPersonas();
                        }
                        catch (Exception) { personaStatus.text = "The card service returned an unreadable response."; }
                    });
                }
                catch (Exception error) { personaStatus.text = "The card could not be read: " + error.Message; }
            });
            Add(personaCard, "Export selected character card", () =>
            {
                int index = personaSelect.choices.IndexOf(personaSelect.value);
                if (index < 0 || index >= personaIds.Count || string.IsNullOrEmpty(personaIds[index])) { personaStatus.text = "Choose a character role first."; return; }
                backend.Api("/api/personas/export", new JObject { ["persona_id"] = personaIds[index] }.ToString(), (json, error) =>
                {
                    if (!alive()) return;
                    if (error != null) { personaStatus.text = error; return; }
                    try
                    {
                        var data = JObject.Parse(json);
                        if (data.Value<bool?>("ok") == false) { personaStatus.text = (string)data["error"] ?? "The card could not be exported."; return; }
                        personaStatus.text = "Exported to " + ((string)data["path"] ?? "the character-cards folder") + ".";
                    }
                    catch (Exception) { personaStatus.text = "The card service returned an unreadable response."; }
                });
            });
            bool deleteArmed = false;
            Button deletePersona = null;
            deletePersona = Add(personaCard, "Delete selected imported card", () =>
            {
                int index = personaSelect.choices.IndexOf(personaSelect.value);
                if (index < 0 || index >= personaIds.Count || string.IsNullOrEmpty(personaIds[index])) { personaStatus.text = "Choose an imported character card first."; return; }
                if (!deleteArmed) { deleteArmed = true; deletePersona.text = "Click again to delete this card"; personaStatus.text = "This removes the imported card from this PC. Click again to confirm."; return; }
                deleteArmed = false; deletePersona.text = "Delete selected imported card";
                backend.Api("/api/personas/delete", new JObject { ["persona_id"] = personaIds[index] }.ToString(), (json, error) =>
                {
                    if (!alive()) return;
                    if (error != null) { personaStatus.text = error; return; }
                    personaStatus.text = "Imported card deleted."; LoadPersonas();
                });
            });
            deletePersona.AddToClassList("agent-reject");

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

            var guestCard = Card("Guest session and local SDK");
            var guest = new Toggle("Guest session (owner memory and tools paused)"); guestCard.Add(guest);
            var guestStatus = Status(guestCard);
            var sdkToken = new TextField("Local SDK token") { isPasswordField = true, isReadOnly = true }; guestCard.Add(sdkToken);
            var sdkStatus = Status(guestCard);
            backend.Api("/api/features/status", null, (json, error) =>
            {
                if (!alive() || error != null) return;
                try { guest.SetValueWithoutNotify(JObject.Parse(json).Value<bool?>("guest") == true); }
                catch (Exception) { }
            });
            guest.RegisterValueChangedCallback(evt =>
            {
                backend.Api("/api/features/guest", new JObject { ["enabled"] = evt.newValue }.ToString(), (json, error) =>
                {
                    if (!alive()) return;
                    if (error != null) { guest.SetValueWithoutNotify(!evt.newValue); guestStatus.text = error; return; }
                    try { guestStatus.text = JObject.Parse(json).Value<bool?>("guest") == true ? "Guest session active. Owner memory and tools are paused." : "Owner session restored."; }
                    catch (Exception) { guestStatus.text = evt.newValue ? "Guest session active." : "Owner session restored."; }
                });
            });
            Add(guestCard, "Create local SDK token", () => backend.Api("/api/features/sdk-token", "{}", (json, error) =>
            {
                if (!alive()) return;
                if (error != null) { sdkStatus.text = error; return; }
                try { sdkToken.value = (string)JObject.Parse(json)["token"] ?? ""; sdkStatus.text = "Token created. Keep it private; it is stateless chat only."; }
                catch (Exception) { sdkStatus.text = "The SDK service returned an unreadable response."; }
            }));
            Add(guestCard, "Revoke SDK token", () => backend.ApiDelete("/api/features/sdk-token", (_, error) =>
            {
                if (!alive()) return;
                sdkToken.value = ""; sdkStatus.text = error ?? "SDK access revoked.";
            }));

            var homeCard = Card("Smart home");
            var homeEnabled = new Toggle("Enable Zigbee2MQTT"); homeCard.Add(homeEnabled);
            var homeHost = new TextField("Broker IP"); homeCard.Add(homeHost);
            var homePort = new TextField("Port (1883 or 8883)"); homeCard.Add(homePort);
            var homeTls = new Toggle("TLS"); homeCard.Add(homeTls);
            var homeUser = new TextField("Username"); homeCard.Add(homeUser);
            var homePassword = new TextField("Password") { isPasswordField = true }; homeCard.Add(homePassword);
            var homeTopic = new TextField("Base topic"); homeCard.Add(homeTopic);
            var homeAllowed = new TextField("Allowed device IDs (comma separated)"); homeCard.Add(homeAllowed);
            var homeStatus = Status(homeCard);
            void LoadHome()
            {
                backend.Api("/api/companion/studio/home", null, (json, error) =>
                {
                    if (!alive() || error != null) { if (alive()) homeStatus.text = error ?? ""; return; }
                    try
                    {
                        var data = JObject.Parse(json); var config = data["config"] as JObject ?? new JObject();
                        homeEnabled.SetValueWithoutNotify(config.Value<bool?>("enabled") == true);
                        homeHost.SetValueWithoutNotify((string)config["host"] ?? "127.0.0.1");
                        homePort.SetValueWithoutNotify(((int?)config["port"] ?? 1883).ToString());
                        homeTls.SetValueWithoutNotify(config.Value<bool?>("tls") == true);
                        homeUser.SetValueWithoutNotify((string)config["username"] ?? "");
                        homeTopic.SetValueWithoutNotify((string)config["base_topic"] ?? "zigbee2mqtt");
                        homeAllowed.SetValueWithoutNotify(string.Join(", ", (config["allowed"] as JArray ?? new JArray()).Values<string>()));
                        homeStatus.text = data.Value<bool?>("connected") == true ? "Connected to " + ((string)config["host"] ?? "broker") + "." : (string)data["error"] ?? "Not connected.";
                    }
                    catch (Exception) { homeStatus.text = "Smart-home settings returned an unreadable response."; }
                });
            }
            Add(homeCard, "Reload smart home", LoadHome);
            Add(homeCard, "Save smart home", () =>
            {
                if (!int.TryParse(homePort.value, out int port) || port != 1883 && port != 8883) { homeStatus.text = "Use MQTT port 1883 or TLS port 8883."; return; }
                var config = new JObject
                {
                    ["enabled"] = homeEnabled.value, ["host"] = homeHost.value.Trim(), ["port"] = port,
                    ["tls"] = homeTls.value, ["username"] = homeUser.value.Trim(), ["base_topic"] = homeTopic.value.Trim(),
                    ["allowed"] = new JArray(homeAllowed.value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).Where(value => value.Length > 0))
                };
                var body = new JObject { ["config"] = config };
                if (!string.IsNullOrEmpty(homePassword.value)) body["password"] = homePassword.value;
                homePassword.value = "";
                backend.Api("/api/companion/studio/home", body.ToString(), (json, error) =>
                {
                    if (!alive()) return;
                    if (error != null) { homeStatus.text = error; return; }
                    try { var data = JObject.Parse(json); homeStatus.text = data.Value<bool?>("connected") == true ? "Smart home connected." : (string)data["error"] ?? "Saved. Broker is not connected."; }
                    catch (Exception) { homeStatus.text = "Smart-home settings saved."; }
                });
            });
            LoadHome();
        }
    }
}
