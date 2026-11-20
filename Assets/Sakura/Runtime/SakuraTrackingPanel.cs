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
                Toggle("Hand tracking · arms and fingers", tracking.Hands, tracking.SetHandsTracking, card);
                var actions = new VisualElement(); actions.AddToClassList("row"); card.Add(actions);
                AddButton("Start tracking", () => tracking.StartSelectedTracking(camera.value), actions);
                AddButton("Calibrate neutral face", tracking.Calibrate, actions);
                AddButton("Stop tracking", tracking.StopTracking, actions);
                card.Add(status);
                var advanced = new Foldout { text = "Tracking source & smoothing", value = false }; card.Add(advanced);
                source = new DropdownField("Face tracking source", sources.ToList(), (int)tracking.Source); advanced.Add(source);
                source.RegisterValueChangedCallback(_ => tracking.SetSource((SakuraTracking.InputSource)source.index));
                var smoothing = new SakuraSlider("Movement smoothing", .01f, .25f) { value = tracking.Smoothing, showInputField = true };
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
            bool forgetArmed = false;
            var forgetAll = new Button();
            forgetAll.text = "Forget all phones & turn off";
            forgetAll.clicked += () =>
            {
                if (!forgetArmed) { forgetArmed = true; forgetAll.text = "Click again to forget every phone"; return; }
                forgetArmed = false; forgetAll.text = "Forget all phones & turn off";
                tracking.PhoneLink("{\"forget_all\":true}");
            };
            phone.Add(forgetAll);
            var permissions = new Foldout { text = "Phone permissions", value = false }; phone.Add(permissions);
            var permissionStatus = new Label(); permissionStatus.AddToClassList("description");
            var system = new Toggle("Allow paired phones to use PC system tools");
            var clipboard = new Toggle("Allow paired phones to read and write the PC clipboard");
            permissions.Add(system); permissions.Add(clipboard); permissions.Add(permissionStatus);
            bool reading = false;
            void LoadPermissions()
            {
                if (reading) return;
                reading = true;
                system.SetEnabled(false); clipboard.SetEnabled(false);
                tracking.PhonePermissions(null, (json, error) =>
                {
                    reading = false;
                    permissionStatus.text = error ?? "";
                    if (error != null) return;
                    try
                    {
                        var settings = JObject.Parse(json);
                        system.SetValueWithoutNotify(settings.Value<bool>("phone_system_access_enabled"));
                        clipboard.SetValueWithoutNotify(settings.Value<bool>("phone_clipboard_enabled"));
                        system.SetEnabled(true); clipboard.SetEnabled(true);
                    }
                    catch (Newtonsoft.Json.JsonException) { permissionStatus.text = "Could not read phone permissions."; }
                });
            }
            void BindPermission(Toggle toggle, string key)
            {
                toggle.SetEnabled(false);
                toggle.RegisterValueChangedCallback(evt =>
                {
                    toggle.SetEnabled(false);
                    tracking.PhonePermissions(new JObject { [key] = evt.newValue }.ToString(), (_, error) =>
                    {
                        permissionStatus.text = error ?? "Permission saved.";
                        if (error != null) toggle.SetValueWithoutNotify(evt.previousValue);
                        toggle.SetEnabled(true);
                    });
                });
            }
            BindPermission(system, "phone_system_access_enabled");
            BindPermission(clipboard, "phone_clipboard_enabled");
            AddButton("Refresh permissions", LoadPermissions, permissions);
            permissions.RegisterValueChangedCallback(evt => { if (evt.target == permissions && evt.newValue) LoadPermissions(); });
            Description("On Sakura Mobile, choose this PC from the list and enter the pairing code. Both devices must be on the same Wi-Fi. Compare the certificate fingerprint when pairing. When you tap Add model on the phone, Sakura opens this PC library so you can choose a PMX, VRM or Live2D model to send.", phone);
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
