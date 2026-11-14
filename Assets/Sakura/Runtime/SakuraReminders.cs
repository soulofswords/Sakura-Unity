using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraReminders : MonoBehaviour
    {
        SakuraBackend backend;
        VisualElement banner;
        Label text;
        string currentId;
        bool polling;
        float nextPoll;
        int generation;

        public void Initialize(SakuraBackend service, VisualElement root)
        {
            backend = service;
            if (banner != null || root == null) return;
            banner = new VisualElement { name = "sakura-reminder-banner" };
            banner.style.position = Position.Absolute;
            banner.style.left = 14; banner.style.right = 14; banner.style.bottom = 78;
            banner.style.paddingTop = 10; banner.style.paddingBottom = 10; banner.style.paddingLeft = 12; banner.style.paddingRight = 12;
            banner.style.backgroundColor = new Color(.16f, .22f, .29f, .97f);
            banner.style.borderTopLeftRadius = 12; banner.style.borderTopRightRadius = 12;
            banner.style.borderBottomLeftRadius = 12; banner.style.borderBottomRightRadius = 12;
            banner.style.display = DisplayStyle.None;
            text = new Label(); text.style.whiteSpace = WhiteSpace.Normal; text.style.color = Color.white; banner.Add(text);
            var actions = new VisualElement(); actions.style.flexDirection = FlexDirection.Row; actions.style.marginTop = 6; banner.Add(actions);
            actions.Add(new Button(() => Dismiss(false)) { text = "Dismiss" });
            actions.Add(new Button(() => Dismiss(true)) { text = "Snooze 5 min" });
            root.Add(banner);
        }

        void Update()
        {
            if (backend == null || backend.Mode == "Agent" || !backend.Connected || polling || Time.unscaledTime < nextPoll) return;
            polling = true; nextPoll = Time.unscaledTime + 1.5f; int revision = generation;
            backend.Api("/api/reminders", null, (json, error) =>
            {
                polling = false;
                if (revision != generation || this == null) return;
                if (error != null) return;
                try
                {
                    var data = JObject.Parse(json);
                    var ringing = (data["items"] as JArray)?.OfType<JObject>().FirstOrDefault(item => (string)item["status"] == "ringing");
                    if (ringing != null)
                    {
                        currentId = (string)ringing["id"];
                        string kind = (string)ringing["kind"] == "timer" ? "Timer finished" : "Alarm";
                        text.text = kind + ": " + ((string)ringing["label"] ?? "reminder");
                        banner.style.display = DisplayStyle.Flex;
                        return;
                    }
                    var nudge = data["archaeology_nudge"] as JObject;
                    if (nudge != null && !string.IsNullOrEmpty((string)nudge["message"]))
                    {
                        currentId = null;
                        text.text = (string)nudge["message"];
                        banner.style.display = DisplayStyle.Flex;
                    }
                    else if (currentId == null) banner.style.display = DisplayStyle.None;
                }
                catch (Exception) { }
            });
        }

        void Dismiss(bool snooze)
        {
            banner.style.display = DisplayStyle.None;
            if (string.IsNullOrEmpty(currentId)) { currentId = null; return; }
            string id = currentId; currentId = null; generation++;
            backend.Api("/api/reminders/dismiss", new JObject { ["id"] = id, ["snooze"] = snooze }.ToString(), (_, __) => { });
        }

        void OnDestroy() { generation++; if (banner != null) banner.RemoveFromHierarchy(); }
    }
}
