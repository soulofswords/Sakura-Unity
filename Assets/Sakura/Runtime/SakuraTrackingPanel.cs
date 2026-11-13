using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraTrackingPanel
    {
        public SakuraTrackingPanel(SakuraTracking tracking, VisualElement parent, bool phoneOnly = false)
        {
            var owner = new VisualElement(); parent.Add(owner);
            var status = new Label(tracking.Status); status.AddToClassList("description");
            DropdownField source = null;
            string[] sources = { "Off", "Mouse cursor", "Sakura Mobile", "NVIDIA Broadcast Tracker", "Sakura native RTX tracker", "VBridger / VMC", "Webcam" };
            var phone = new Foldout { text = "Connect Sakura Mobile", value = phoneOnly };
            if (!phoneOnly)
            {
                var card = Card("Track my face & hands", owner);
                Description("Use your expressions, gaze and head to animate your character. Face and shoulder tracking stay active when hands are switched off.", card);
                var cameras = WebCamTexture.devices.Select(device => device.name).ToList(); cameras.Add("Sakura Mobile");
                var camera = new DropdownField("Camera", cameras, tracking.Source == SakuraTracking.InputSource.Phone ? cameras.Count - 1 : 0); card.Add(camera);
                camera.RegisterValueChangedCallback(_ =>
                {
                    if (camera.value == "Sakura Mobile") tracking.SetSource(SakuraTracking.InputSource.Phone);
                    else if (tracking.Source == SakuraTracking.InputSource.Phone || tracking.Source == SakuraTracking.InputSource.Off) tracking.SetSource(SakuraTracking.InputSource.Webcam);
                });
                AddButton("Connect to Phone", () => { tracking.PhoneLink(); phone.value = true; }, card);
                Toggle("Track mouth", tracking.Mouth, enabled => tracking.Mouth = enabled, card);
                Toggle("Shoulders & body movement", tracking.Pose, enabled => tracking.Pose = enabled, card);
                Toggle("Hand tracking · arms and fingers", tracking.Hands, enabled =>
                {
                    tracking.Hands = enabled;
                    if (tracking.Source == SakuraTracking.InputSource.Phone) tracking.PhoneControl("hands", enabled);
                }, card);
                var actions = new VisualElement(); actions.AddToClassList("row"); card.Add(actions);
                AddButton("Start tracking", () =>
                {
                    if (camera.value == "Sakura Mobile") tracking.SetSource(SakuraTracking.InputSource.Phone);
                    else
                    {
                        if (tracking.Source == SakuraTracking.InputSource.Off || tracking.Source == SakuraTracking.InputSource.Phone) tracking.SetSource(SakuraTracking.InputSource.Webcam);
                        tracking.StartCamera(camera.value);
                    }
                }, actions);
                AddButton("Calibrate neutral face", tracking.Calibrate, actions);
                AddButton("Stop tracking", () => tracking.SetSource(SakuraTracking.InputSource.Off), actions);
                card.Add(status);
                var advanced = new Foldout { text = "Tracking source & smoothing", value = false }; card.Add(advanced);
                source = new DropdownField("Face tracking source", sources.ToList(), (int)tracking.Source); advanced.Add(source);
                source.RegisterValueChangedCallback(_ => tracking.SetSource((SakuraTracking.InputSource)source.index));
                var smoothing = new Slider("Movement smoothing", .01f, .25f) { value = tracking.Smoothing, showInputField = true };
                smoothing.RegisterValueChangedCallback(change => tracking.Smoothing = change.newValue); advanced.Add(smoothing);
                Description("Webcam uses local CPU MediaPipe. Native RTX requires the installed NVIDIA AR SDK. NVIDIA Broadcast Tracker uses the configured ExpressionApp engine. VBridger receives local VMC data on UDP 39539.", advanced);
            }
            var phoneCard = Card("Android · Phone connection", owner); phoneCard.Add(phone);
            var info = new Label(tracking.PhoneInfo); info.AddToClassList("description"); phone.Add(info);
            var phoneActions = new VisualElement(); phoneActions.AddToClassList("row"); phone.Add(phoneActions);
            AddButton("Enable phone connection", () => tracking.PhoneLink("{\"enabled\":true}"), phoneActions);
            AddButton("Existing devices", () => tracking.PhoneLink(), phoneActions);
            AddButton("New device", () => tracking.PhoneLink("{\"new\":true}"), phoneActions);
            AddButton("Turn off phone connection", () => tracking.PhoneLink("{\"enabled\":false}"), phone);
            Description("On Sakura Mobile, choose this PC from the list and enter the pairing code. Both devices must be on the same Wi-Fi. Compare the certificate fingerprint when pairing.", phone);
            var remembered = new VisualElement(); phone.Add(remembered);
            void Refresh()
            {
                status.text = tracking.Status; info.text = tracking.PhoneInfo;
                source?.SetValueWithoutNotify(sources[(int)tracking.Source]);
                remembered.Clear();
                foreach (var device in tracking.PhoneDevices.OfType<JObject>())
                {
                    string id = (string)device["id"];
                    if (string.IsNullOrEmpty(id)) continue;
                    var row = new VisualElement(); row.AddToClassList("row"); remembered.Add(row);
                    row.Add(new Label(((string)device["name"] ?? "Android device") + (device.Value<bool>("connected") ? " · Connected" : device.Value<bool>("pairable") ? " · Remembered" : " · Choose New device")));
                    AddButton("Forget", () => tracking.PhoneLink(new JObject { ["forget"] = id }.ToString()), row);
                }
            }
            tracking.Changed += Refresh;
            owner.RegisterCallback<DetachFromPanelEvent>(_ => tracking.Changed -= Refresh);
            Refresh();
        }
        private static VisualElement Card(string title, VisualElement parent)
        {
            var card = new VisualElement(); card.AddToClassList("card"); parent.Add(card);
            var label = new Label(title); label.AddToClassList("card-title"); card.Add(label); return card;
        }
        private static void Description(string text, VisualElement parent) { var label = new Label(text); label.AddToClassList("description"); parent.Add(label); }
        private static void AddButton(string text, Action action, VisualElement parent) => parent.Add(new Button(action) { text = text });
        private static void Toggle(string title, bool initial, Action<bool> action, VisualElement parent)
        {
            var toggle = new Toggle(title) { value = initial }; toggle.RegisterValueChangedCallback(change => action(change.newValue)); parent.Add(toggle);
        }
    }
}
