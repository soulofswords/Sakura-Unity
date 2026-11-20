using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraOutputPanel
    {
        public SakuraOutputPanel(SakuraOutput output, VisualElement parent)
        {
            var owner = new VisualElement(); parent.Add(owner);
            var capture = Card("Output quality", owner);
            var quality = new DropdownField("Quality", new List<string> { "Low — older GPUs", "Balanced — GTX 10 series target", "High — RTX 3060+ target" }, (int)output.Preset); capture.Add(quality);
            quality.RegisterValueChangedCallback(_ => output.SetQuality((SakuraOutput.Quality)quality.index));
            var rate = new DropdownField("Capture target FPS", new List<string> { "15", "30", "60" }, output.FrameRate == 15 ? 0 : output.FrameRate == 30 ? 1 : 2); capture.Add(rate);
            rate.RegisterValueChangedCallback(_ => output.SetFrameRate(int.Parse(rate.value)));
            var name = new TextField("Sender name") { value = "Sakura" }; capture.Add(name);
            var states = new List<Label>(); var buttons = new List<Button>();
            Action refreshCamera = null;
            foreach (var channel in output.Channels)
            {
                var row = Card(channel.Id == "spout" ? "Spout 2" : channel.Id == "ndi" ? "NDI" : "Virtual camera", owner);
                var state = new Label(); states.Add(state); row.Add(state);
                var button = new Button(() => output.Configure(channel.Id, !channel.Enabled, name.value)); buttons.Add(button); row.Add(button);
                if (channel.Id == "virtual-camera")
                    refreshCamera = CameraDriver(output, channel, row, button);
            }
            var recording = Card("Screenshots & recording", owner);
            recording.Add(new Button(output.Screenshot) { text = "Save PNG screenshot" });
            var encoder = new TextField("FFmpeg executable") { value = output.EncoderPath ?? "" }; recording.Add(encoder);
            encoder.RegisterValueChangedCallback(evt => output.EncoderPath = evt.newValue);
            var record = new Button(() => { if (output.Recording) output.StopRecording(); else output.StartRecording(); }); recording.Add(record);
            var hint = new Label("Record the avatar stage with transparency. Audio is routed separately in your streaming app."); hint.AddToClassList("description"); recording.Add(hint);
            recording.Add(new Button(() => { try { System.IO.Directory.CreateDirectory(output.MediaFolder); Application.OpenURL(new Uri(output.MediaFolder + System.IO.Path.DirectorySeparatorChar).AbsoluteUri); } catch (Exception error) { Debug.LogWarning(error.Message); } }) { text = "Open captures folder" });
            var status = new Label(); status.style.whiteSpace = WhiteSpace.Normal; owner.Add(status);
            void Refresh()
            {
                if (output == null) return;
                quality.SetValueWithoutNotify(quality.choices[(int)output.Preset]); rate.SetValueWithoutNotify(output.FrameRate.ToString());
                for (int i = 0; i < output.Channels.Length; i++)
                {
                    var channel = output.Channels[i]; states[i].text = channel.Id + ": " + (channel.Enabled ? "sending; " + channel.Frames + " accepted frames" : "off");
                    buttons[i].text = channel.Busy ? "Working…" : channel.Enabled ? "Stop " + channel.Id : "Start " + channel.Id; buttons[i].SetEnabled(!channel.Busy);
                }
                record.text = output.SavingRecording ? "Finishing…" : output.Recording ? "Stop recording" : "Record WebM"; record.SetEnabled(!output.SavingRecording);
                status.text = output.Status;
                refreshCamera?.Invoke();
            }
            output.Changed += Refresh; owner.RegisterCallback<DetachFromPanelEvent>(_ => { if (output != null) output.Changed -= Refresh; }); Refresh();
        }

        private static Action CameraDriver(SakuraOutput output, SakuraOutput.Channel channel, VisualElement row, Button start)
        {
            const string route = "/api/unity/camera-driver";
            var status = new Label("Checking camera driver…"); status.AddToClassList("description"); row.Add(status);
            bool installed = false, available = false, busy = false, removeArmed = false;
            var install = new Button { text = "Install camera driver" };
            var remove = new Button { text = "Remove camera driver" };
            var check = new Button { text = "Check camera driver" };
            row.Add(install); row.Add(remove); row.Add(check);
            void Refresh()
            {
                var idle = !busy && !channel.Busy && !channel.Enabled;
                install.SetEnabled(idle && available && !installed);
                remove.SetEnabled(idle && installed);
                check.SetEnabled(!busy);
                start.SetEnabled(!busy && !channel.Busy && (channel.Enabled || installed));
            }
            void Request(string action)
            {
                if (busy || (action != null && (channel.Busy || channel.Enabled))) return;
                removeArmed = false; remove.text = "Remove camera driver";
                busy = true; status.text = action == null ? "Checking camera driver…" : "Waiting for Windows camera setup…"; Refresh();
                output.Backend.Api(route, action == null ? null : new JObject { ["action"] = action }.ToString(), (json, error) =>
                {
                    busy = false;
                    if (error != null) status.text = error;
                    else
                    {
                        try
                        {
                            var state = JObject.Parse(json);
                            installed = state.Value<bool>("installed");
                            available = state.Value<bool?>("available") ?? available;
                            status.text = installed ? "Sakura Virtual Camera is installed. Reopen your video app after installation." :
                                available ? "Install the camera driver before starting output." : "This build does not include a Windows camera driver.";
                        }
                        catch (Newtonsoft.Json.JsonException) { status.text = "Could not read camera driver status."; }
                    }
                    Refresh();
                });
            }
            install.clicked += () => Request("install");
            remove.clicked += () =>
            {
                if (removeArmed) Request("remove");
                else { removeArmed = true; remove.text = "Confirm driver removal"; }
            };
            check.clicked += () => Request(null);
            row.schedule.Execute(() => Request(null));
            Refresh();
            return Refresh;
        }

        private static VisualElement Card(string title, VisualElement parent)
        {
            var card = new VisualElement(); card.AddToClassList("card"); parent.Add(card);
            var label = new Label(title); label.AddToClassList("card-title"); card.Add(label); return card;
        }
    }
}
