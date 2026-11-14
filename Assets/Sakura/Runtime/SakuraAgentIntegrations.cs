using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed partial class SakuraAgentPanel
    {
        private void BuildIntegrations()
        {
            Text("Accounts, local knowledge and Agent health.", content, "card-title");

            var calendar = new Foldout { text = "Google Calendar", value = true }; content.Add(calendar);
            var calendarStatus = new Label(); calendarStatus.style.whiteSpace = WhiteSpace.Normal; calendar.Add(calendarStatus);
            var calendarClient = new TextField("Google OAuth client ID") { value = "" }; calendar.Add(calendarClient);
            var calendarSecret = new TextField("Google OAuth client secret") { isPasswordField = true }; calendar.Add(calendarSecret);
            void LoadCalendar()
            {
                Request("/api/agent/calendar/status", null, data =>
                {
                    calendarStatus.text = (data.Value<bool?>("connected") == true ? "Connected" : data.Value<bool?>("configured") == true ? "Configured, not connected" : "Not configured") +
                        " · calendar " + ((string)data["calendar"] ?? "primary") +
                        (data.Value<bool?>("reconnect_required") == true ? " · sign in again" : "") +
                        (data.Value<bool?>("workspace_connected") == true ? " · workspace access" : "");
                });
            }
            Button("Refresh calendar status", LoadCalendar, calendar);
            Button("Save calendar OAuth app", () => Request("/api/agent/calendar/configure", new JObject { ["client_id"] = (calendarClient.value ?? "").Trim(), ["client_secret"] = calendarSecret.value ?? "" }, result =>
            {
                calendarSecret.SetValueWithoutNotify("");
                calendarStatus.text = "Calendar OAuth app saved. Connect the account next.";
            }), calendar);
            Button("Connect Google Calendar", () => Request("/api/agent/calendar/connect", new JObject(), result =>
            {
                calendarStatus.text = "Browser opened. Finish Google sign-in, then refresh.";
            }), calendar);
            Button("Disconnect Google Calendar", () => Request("/api/agent/calendar/disconnect", new JObject(), result =>
            {
                calendarStatus.text = "Google Calendar disconnected.";
            }), calendar);
            Button("Show next 7 days", () => Request("/api/agent/calendar/agenda", null, data =>
            {
                var events = (data["events"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                calendarStatus.text = events.Count == 0 ? "No events in the next 7 days." : string.Join(" | ", events.Take(10).Select(item =>
                    ((string)item["summary"] ?? "Event") + " · " + ((string)item["start"] ?? "") + (string.IsNullOrEmpty((string)item["start"]) ? "" : " → " + ((string)item["end"] ?? ""))));
            }), calendar);
            LoadCalendar();

            var linkedin = new Foldout { text = "LinkedIn", value = true }; content.Add(linkedin);
            var linkedinStatus = new Label(); linkedinStatus.style.whiteSpace = WhiteSpace.Normal; linkedin.Add(linkedinStatus);
            var linkedinClient = new TextField("LinkedIn client ID") { value = "" }; linkedin.Add(linkedinClient);
            var linkedinSecret = new TextField("LinkedIn client secret") { isPasswordField = true }; linkedin.Add(linkedinSecret);
            void LoadLinkedIn()
            {
                Request("/api/agent/state", null, data =>
                {
                    var item = data["linkedin"] as JObject ?? new JObject();
                    linkedinStatus.text = item.Value<bool?>("connected") == true ? "Connected" : item.Value<bool?>("configured") == true ? "Configured, not connected" : "Not configured";
                    if ((item["scopes"] as JArray ?? new JArray()).Count > 0) linkedinStatus.text += " · " + string.Join(", ", item["scopes"].Values<string>());
                });
            }
            Button("Refresh LinkedIn status", LoadLinkedIn, linkedin);
            Button("Save LinkedIn OAuth app", () => Request("/api/agent/linkedin/configure", new JObject { ["client_id"] = (linkedinClient.value ?? "").Trim(), ["client_secret"] = linkedinSecret.value ?? "" }, result =>
            {
                linkedinSecret.SetValueWithoutNotify("");
                linkedinStatus.text = "LinkedIn OAuth app saved. Connect the account next.";
            }), linkedin);
            Button("Connect LinkedIn", () => Request("/api/agent/linkedin/connect", new JObject(), result =>
            {
                string url = (string)result["url"] ?? "";
                if (string.IsNullOrEmpty(url)) { linkedinStatus.text = "LinkedIn did not return a sign-in URL."; return; }
                Application.OpenURL(url);
                linkedinStatus.text = "Browser opened. Finish LinkedIn sign-in, then refresh.";
            }), linkedin);
            Button("Disconnect LinkedIn", () => Request("/api/agent/linkedin/disconnect", new JObject(), result =>
            {
                linkedinStatus.text = "LinkedIn disconnected.";
            }), linkedin);
            LoadLinkedIn();

            var knowledge = new Foldout { text = "Local knowledge folders", value = true }; content.Add(knowledge);
            var knowledgeStatus = new Label(); knowledgeStatus.style.whiteSpace = WhiteSpace.Normal; knowledge.Add(knowledgeStatus);
            var knowledgeFolder = new TextField("Folder on this PC"); knowledge.Add(knowledgeFolder);
            var knowledgeSemantic = new Toggle("Use local meaning search when available"); knowledge.Add(knowledgeSemantic);
            var knowledgeList = new VisualElement(); knowledge.Add(knowledgeList);
            void LoadKnowledge()
            {
                Request("/api/agent/knowledge/folders", null, data =>
                {
                    knowledgeList.Clear();
                    var folders = (data["folders"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                    foreach (var folder in folders)
                    {
                        string root = (string)folder["root"] ?? "";
                        var row = new VisualElement(); row.style.flexDirection = FlexDirection.Row; knowledgeList.Add(row);
                        var label = new Label(root); label.style.flexGrow = 1; row.Add(label);
                        row.Add(new Button(() => Request("/api/agent/knowledge/remove-folder", new JObject { ["folder"] = root }, result => { knowledgeStatus.text = "Folder removed from the local index."; LoadKnowledge(); })) { text = "Remove" });
                    }
                    knowledgeStatus.text = folders.Count == 0 ? "No indexed folders." : folders.Count + " indexed folder(s). Originals are never modified.";
                });
            }
            Button("Refresh indexed folders", LoadKnowledge, knowledge);
            Button("Index folder", () => Request("/api/agent/knowledge/folders", new JObject { ["folder"] = (knowledgeFolder.value ?? "").Trim(), ["semantic"] = knowledgeSemantic.value }, result =>
            {
                knowledgeStatus.text = "Folder indexed. Review the manifest and approve the consent prompt if shown.";
                LoadKnowledge();
            }), knowledge);
            var knowledgeQuery = new TextField("Search indexed folders"); knowledge.Add(knowledgeQuery);
            Button("Search folders", () => Request("/api/agent/knowledge/search-folders", new JObject { ["query"] = (knowledgeQuery.value ?? "").Trim() }, data =>
            {
                var rows = (data["results"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                knowledgeStatus.text = rows.Count == 0 ? "No matching indexed text." : string.Join(" | ", rows.Take(8).Select(row => ((string)row["text"] ?? "Result") + " · " + ((string)row["source"] ?? "")));
            }), knowledge);
            LoadKnowledge();

            var messaging = new Foldout { text = "Discord, Slack & WhatsApp", value = false }; content.Add(messaging);
            void AddGateway(string provider, string title)
            {
                var gateway = new Foldout { text = title, value = false }; messaging.Add(gateway);
                var gatewayStatus = new Label(); gatewayStatus.style.whiteSpace = WhiteSpace.Normal; gateway.Add(gatewayStatus);
                var enabled = new Toggle("Enable this gateway"); gateway.Add(enabled);
                var channels = new TextField("Allowed channel IDs (comma-separated)"); gateway.Add(channels);
                var users = new TextField("Allowed user IDs (comma-separated)"); gateway.Add(users);
                var token = new TextField("Bot token (blank keeps the saved token)") { isPasswordField = true }; gateway.Add(token);
                var phone = new TextField("WhatsApp phone-number ID"); gateway.Add(phone);
                var appSecret = new TextField("WhatsApp app secret") { isPasswordField = true }; gateway.Add(appSecret);
                var verifyToken = new TextField("WhatsApp webhook verify token") { isPasswordField = true }; gateway.Add(verifyToken);
                void LoadGateway()
                {
                    Request("/api/agent/gateways", null, data =>
                    {
                        var row = (data as JArray ?? new JArray()).OfType<JObject>().FirstOrDefault(item => (string)item["provider"] == provider);
                        if (row == null) { gatewayStatus.text = "No status returned."; return; }
                        enabled.SetValueWithoutNotify(row.Value<bool?>("enabled") == true);
                        channels.SetValueWithoutNotify(string.Join(",", (row["channels"] as JArray ?? new JArray()).Values<string>()));
                        users.SetValueWithoutNotify(string.Join(",", (row["users"] as JArray ?? new JArray()).Values<string>()));
                        phone.SetValueWithoutNotify((string)row["phone_id"] ?? "");
                        gatewayStatus.text = (row.Value<bool?>("enabled") == true ? (row.Value<bool?>("running") == true ? "Running" : "Enabled, stopped") : "Off") +
                            " · token " + (row.Value<bool?>("token_set") == true ? "saved" : "missing") +
                            (provider == "whatsapp" ? " · app secret " + (row.Value<bool?>("app_secret_set") == true ? "saved" : "missing") + " · verify token " + (row.Value<bool?>("verify_token_set") == true ? "saved" : "missing") : "") +
                            ((string)row["last_error"] is string error && error.Length > 0 ? " · " + error : "");
                    });
                }
                Button("Refresh status", LoadGateway, gateway);
                Button("Save gateway", () =>
                {
                    var body = new JObject
                    {
                        ["enabled"] = enabled.value,
                        ["channels"] = new JArray((channels.value ?? "").Split(',').Select(value => value.Trim()).Where(value => value.Length > 0)),
                        ["users"] = new JArray((users.value ?? "").Split(',').Select(value => value.Trim()).Where(value => value.Length > 0)),
                        ["token"] = (token.value ?? "").Trim(),
                        ["phone_id"] = (phone.value ?? "").Trim(),
                        ["app_secret"] = (appSecret.value ?? "").Trim(),
                        ["verify_token"] = (verifyToken.value ?? "").Trim(),
                        ["clear_token"] = false
                    };
                    Request("/api/agent/gateways/" + provider, body, result =>
                    {
                        token.SetValueWithoutNotify(""); appSecret.SetValueWithoutNotify(""); verifyToken.SetValueWithoutNotify("");
                        gatewayStatus.text = "Gateway settings saved.";
                        LoadGateway();
                    });
                }, gateway);
                LoadGateway();
            }
            AddGateway("discord", "Discord");
            AddGateway("slack", "Slack");
            AddGateway("whatsapp", "WhatsApp");

            var features = new Foldout { text = "Agent personal features", value = false }; content.Add(features);
            var phoneMemory = new Toggle("Allow paired phones to copy saved references"); features.Add(phoneMemory);
            var phoneHistory = new Toggle("Allow paired phones to exchange conversation history"); features.Add(phoneHistory);
            var cameraStyle = new Toggle("Use explicit camera delivery hints"); features.Add(cameraStyle);
            var sharedContext = new Toggle("Allow manually shared collaboration context"); features.Add(sharedContext);
            var pageRevisit = new Toggle("Allow page revisit suggestions"); features.Add(pageRevisit);
            var featureStatus = new Label(); featureStatus.style.whiteSpace = WhiteSpace.Normal; features.Add(featureStatus);
            void LoadFeatures()
            {
                Request("/api/agent/features", null, data =>
                {
                    phoneMemory.SetValueWithoutNotify(data.Value<bool?>("phone_memory") == true);
                    phoneHistory.SetValueWithoutNotify(data.Value<bool?>("phone_history") == true);
                    cameraStyle.SetValueWithoutNotify(data.Value<bool?>("camera_style") == true);
                    sharedContext.SetValueWithoutNotify(data.Value<bool?>("shared_context") == true);
                    pageRevisit.SetValueWithoutNotify(data.Value<bool?>("page_revisit") == true);
                    featureStatus.text = "Optional personal features; anything that can expose private context is off unless you enable it.";
                });
            }
            Button("Refresh feature switches", LoadFeatures, features);
            Button("Save feature switches", () => Request("/api/agent/features", new JObject
            {
                ["phone_memory"] = phoneMemory.value,
                ["phone_history"] = phoneHistory.value,
                ["camera_style"] = cameraStyle.value,
                ["shared_context"] = sharedContext.value,
                ["page_revisit"] = pageRevisit.value
            }, result => { featureStatus.text = "Personal feature switches saved."; LoadFeatures(); }), features);
            LoadFeatures();

            var health = new Foldout { text = "Agent diagnostics", value = false }; content.Add(health);
            var healthStatus = new Label(); healthStatus.style.whiteSpace = WhiteSpace.Normal; health.Add(healthStatus);
            Button("Run doctor", () => Request("/api/agent/doctor", null, data => healthStatus.text = data.ToString(Formatting.Indented)), health);
            Button("Show recent audit calls", () => Request("/api/agent/audit", null, data => healthStatus.text = data.ToString(Formatting.Indented)), health);
            Button("Show metrics", () => Request("/api/agent/metrics?window=900", null, data => healthStatus.text = data.ToString(Formatting.Indented)), health);
            Button("Show usage configuration", () => Request("/api/agent/usage-config", null, data => healthStatus.text = data.ToString(Formatting.Indented)), health);
            healthStatus.text = "Doctor, audit, metrics and usage details are read-only diagnostics.";
        }
    }
}
