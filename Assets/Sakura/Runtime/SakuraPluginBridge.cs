using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Live2D.Cubism.Core;
using Live2D.Cubism.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    [DefaultExecutionOrder(-5)]
    public sealed class SakuraPluginBridge : MonoBehaviour
    {
        const int MaximumMessage = 128 * 1024;
        SakuraBackend backend;
        SakuraAvatarViewport viewport;
        CharacterEntry[] catalog = Array.Empty<CharacterEntry>();
        bool catalogReady, initialized, running, externalEnabled, configuring, desiredEnabled;
        int desiredPort = 8001, configurationRevision;
        int generation, port = 8001;
        double retryAfter;
        CancellationTokenSource lifetime;
        ClientWebSocket socket;
        readonly ConcurrentQueue<Action> main = new ConcurrentQueue<Action>();
        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        bool lastFace, faceKnown;
        readonly Dictionary<CubismParameter, float> injected = new Dictionary<CubismParameter, float>();
        readonly Dictionary<CubismParameter, (float before, float after)> writes = new Dictionary<CubismParameter, (float, float)>();
        readonly Dictionary<string, float> customValues = new Dictionary<string, float>();
        readonly HashSet<string> selectedArtMeshes = new HashSet<string>();
        float injectedUntil;
        readonly SakuraPluginPhysics physics = new SakuraPluginPhysics();
        GameObject injectionModel;
        public string Status { get; private set; } = "Plugin server is off.";
        public bool Connected => socket != null && socket.State == WebSocketState.Open;
        bool FreshCustomValues => viewport != null && injectionModel != null && viewport.AvatarRoot == injectionModel && Time.unscaledTime < injectedUntil;
        public string[] CustomParameterNames => FreshCustomValues ? customValues.Keys.ToArray() : Array.Empty<string>();
        public bool TryCustomParameter(string name, out float value) { value = 0; return FreshCustomValues && customValues.TryGetValue(name, out value); }
        public void Initialize(SakuraBackend service, SakuraAvatarViewport avatars)
        {
            if (initialized) return;
            backend = service; viewport = avatars; initialized = true;
            backend.CatalogReceived += Catalog; backend.RefreshCatalog();
        }
        void Catalog(CharacterEntry[] entries) { catalog = entries ?? Array.Empty<CharacterEntry>(); catalogReady = true; }
        public static string ModelId(CharacterEntry entry) => "/models/" + string.Join("/", entry.path.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        public static void ValidateMessage(JObject message)
        {
            if (message == null || Encoding.UTF8.GetByteCount(message.ToString(Formatting.None)) > MaximumMessage || message["id"]?.Type != JTokenType.String || message.Value<string>("id").Length > 32 || string.IsNullOrEmpty(message.Value<string>("id")) || !Regex.IsMatch(message.Value<string>("command") ?? "", @"\A[a-z_]{1,64}\z") || message["args"] != null && !(message["args"] is JObject)) throw new ArgumentException("Invalid or oversized plugin command.");
            void Check(JToken token, int depth)
            {
                if (depth > 16) throw new ArgumentException("Plugin arguments are too deeply nested.");
                if (token is JContainer container)
                {
                    if (container.Count > 1024) throw new ArgumentException("Plugin argument collection exceeds 1024 entries.");
                    foreach (var child in container.Children()) Check(child, depth + 1);
                }
                if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer) Number(token, "Plugin number");
                if (token.Type == JTokenType.String && token.Value<string>().Length > 4096) throw new ArgumentException("Plugin text exceeds 4096 characters.");
            }
            Check(message, 0);
        }
        static float Number(JToken token, string name)
        {
            if (token == null || token.Type != JTokenType.Float && token.Type != JTokenType.Integer) throw new ArgumentException(name + " must be numeric.");
            double value = token.Value<double>();
            if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 1000000) throw new ArgumentException(name + " must be finite and within ±1000000.");
            return (float)value;
        }
        static JObject Ok(JObject data = null) => new JObject { ["ok"] = true, ["data"] = data ?? new JObject() };
        static JObject Fail(string error) => new JObject { ["ok"] = false, ["error"] = error.Substring(0, Math.Min(error.Length, 200)) };
        void NeedModel() { if (viewport == null || !viewport.HasAvatar) throw new InvalidOperationException("No character is loaded in the Sakura window."); }
        SakuraModelTools Tools()
        {
            NeedModel(); return viewport.AvatarRoot.GetComponent<SakuraModelTools>() ?? throw new InvalidOperationException("This model has no native material/Live2D tools.");
        }
        SakuraAvatarExpressions Expressions() { NeedModel(); return viewport.Expressions ?? throw new InvalidOperationException("This model has no native expressions."); }
        CubismModel Cubism() { NeedModel(); return viewport.AvatarRoot.GetComponent<CubismModel>() ?? throw new InvalidOperationException("This command requires a Live2D model."); }
        JObject State()
        {
            bool loaded = viewport != null && viewport.HasAvatar;
            return new JObject { ["modelLoaded"] = loaded, ["modelName"] = loaded ? Path.GetFileName(viewport.LoadedModelPath) : "", ["modelID"] = loaded ? ModelId(new CharacterEntry { path = viewport.LoadedModelPath }) : "", ["modelType"] = loaded ? viewport.LoadedFormat : "" };
        }
        public async Task<JObject> Dispatch(JObject message, CancellationToken cancellation = default)
        {
            string id = message?.Value<string>("id") ?? "";
            JObject reply;
            try { cancellation.ThrowIfCancellationRequested(); ValidateMessage(message); reply = await Execute(message.Value<string>("command"), message["args"] as JObject ?? new JObject(), cancellation); }
            catch (Exception error) { reply = Fail(error.Message); }
            reply["id"] = id; return reply;
        }
        async Task<JObject> Execute(string command, JObject args, CancellationToken cancellation)
        {
            switch (command)
            {
                case "vts_state": return Ok(State());
                case "vts_models":
                    if (!catalogReady) throw new InvalidOperationException("The native model catalog is loading. Retry this request.");
                    var models = new JArray(catalog.Select(entry => new JObject { ["modelLoaded"] = viewport != null && viewport.LoadedModelPath == entry.path, ["modelName"] = entry.name, ["modelID"] = ModelId(entry), ["modelType"] = entry.format }));
                    return Ok(new JObject { ["availableModels"] = models, ["numberOfModels"] = models.Count });
                case "vts_model_load":
                    string requested = args.Value<string>("modelID");
                    var model = catalog.FirstOrDefault(entry => ModelId(entry) == requested) ?? throw new ArgumentException("modelID must be an exact path from AvailableModelsRequest.");
                    if (viewport == null || backend == null) throw new InvalidOperationException("The native model viewport is unavailable.");
                    await viewport.LoadModel(backend.SourceFolder, model.path, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    if (!viewport.HasAvatar || viewport.LoadedModelPath != model.path || !string.IsNullOrEmpty(viewport.LastImportError)) throw new InvalidOperationException(viewport.LastImportError ?? "The model could not be loaded.");
                    var loadedState = State(); Emit("ModelLoadedEvent", loadedState); return Ok(loadedState);
                case "vts_model_move": NeedModel(); return Ok(new JObject { ["modelPosition"] = viewport.PluginMove(args) });
                case "get_status":
                    NeedModel(); return Ok(new JObject { ["model_name"] = Path.GetFileName(viewport.LoadedModelPath), ["motion_groups"] = new JArray(), ["expressions"] = new JArray(Expressions().Entries.Where(entry => !entry.Id.StartsWith("parameter:", StringComparison.Ordinal)).Select(entry => entry.Name).Distinct()), ["idle"] = new JObject { ["idle"] = "", ["lost"] = "" } });
                case "vts_expressions": return Ok(new JObject { ["modelLoaded"] = true, ["expressions"] = new JArray(Expressions().Entries.Where(entry => !entry.Id.StartsWith("parameter:", StringComparison.Ordinal)).GroupBy(entry => entry.Name).Select(group => new JObject { ["name"] = group.Key, ["active"] = group.Any(entry => entry.Active), ["isCustom"] = true })) });
                case "set_expression": case "vts_expression":
                    string name = args.Value<string>("name") ?? args.Value<string>("expressionFile"); bool active = args.Value<bool?>("active") != false;
                    var matches = MatchExpression(name);
                    foreach (var entry in matches) { if (active) entry.Strength = viewport.LoadedFormat == "Live2D" ? 1 : .7f; Expressions().SetActive(entry.Id, active); }
                    return Ok(new JObject { ["name"] = name, ["active"] = active });
                case "vts_hotkeys":
                    var hotkeys = new JArray(Expressions().Entries.Where(entry => !entry.Id.StartsWith("parameter:", StringComparison.Ordinal)).GroupBy(entry => entry.Name).Select(group => new JObject { ["name"] = group.Key, ["type"] = "ToggleExpression", ["hotkeyID"] = "expression:" + group.Key, ["action"] = "ToggleExpression", ["file"] = group.Key }));
                    foreach (var action in Expressions().Actions) hotkeys.Add(new JObject { ["name"] = action.Name, ["type"] = "SakuraAction", ["hotkeyID"] = "action:" + action.Id, ["action"] = action.Id });
                    return Ok(new JObject { ["availableHotkeys"] = hotkeys, ["numberOfHotkeys"] = hotkeys.Count });
                case "vts_hotkey":
                    string hotkey = args.Value<string>("hotkeyID") ?? "";
                    if (hotkey.StartsWith("expression:", StringComparison.Ordinal)) foreach (var entry in MatchExpression(hotkey.Substring(11))) Expressions().SetActive(entry.Id, true);
                    else if (hotkey.StartsWith("action:", StringComparison.Ordinal)) Expressions().RunAction(hotkey.Substring(7));
                    else throw new NotSupportedException("Unsupported native hotkeyID. Use an ID from HotkeysInCurrentModelResponse.");
                    return Ok(new JObject { ["hotkeyID"] = hotkey, ["triggered"] = true });
                case "vts_parameters": return Ok(ParameterTable(args));
                case "vts_parameter_value":
                    string parameterName = args.Value<string>("name");
                    var table = ParameterTable(args); var row = ((JArray)table["defaultParameters"]).Concat((JArray)table["customParameters"]).FirstOrDefault(value => value.Value<string>("name") == parameterName) ?? throw new ArgumentException("Unknown parameter: " + parameterName);
                    return Ok(new JObject { ["name"] = parameterName, ["value"] = row["value"], ["valueDefault"] = row["defaultValue"], ["valueMin"] = row["minimum"], ["valueMax"] = row["maximum"] });
                case "vts_inject": return Ok(Inject(args));
                case "vts_artmeshes": return Ok(new JObject { ["artMeshNames"] = new JArray(Cubism().Drawables.Select(drawable => drawable.Id)), ["artMeshCount"] = Cubism().Drawables.Length });
                case "vts_face_found": return Ok(new JObject { ["found"] = GetComponent<SakuraTracking>()?.FaceDetected == true });
                case "vts_statistics": return Ok(new JObject { ["uptime"] = (int)Time.realtimeSinceStartup, ["framerate"] = Time.unscaledDeltaTime > 0 ? 1 / Time.unscaledDeltaTime : 0, ["modelLoaded"] = viewport != null && viewport.HasAvatar, ["tracking"] = TrackingActive() });
                case "vts_folders": return Ok(new JObject { ["vTubeStudioFolder"] = "/models", ["models"] = "/models", ["items"] = "/models", ["backgrounds"] = "/models", ["live2DModels"] = "/models/live2d" });
                case "screenshot":
                    NeedModel(); var avatar = viewport.AvatarRoot; int frame = Time.frameCount; float deadline = Time.realtimeSinceStartup + 5;
                    while (Time.frameCount <= frame + 1)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (viewport.AvatarRoot != avatar || Time.realtimeSinceStartup > deadline) throw new InvalidOperationException("The model changed or stopped rendering before the screenshot was ready.");
                        await Task.Yield();
                    }
                    cancellation.ThrowIfCancellationRequested();
                    string file = viewport.SaveSnapshot("plugin-" + DateTime.UtcNow.Ticks); return Ok(new JObject { ["path"] = file });
                case "vts_physics_get": return Ok(PhysicsInfo(args));
                case "vts_physics_set":
                    NeedModel();
                    if (args.Value<bool?>("_vtsCanonical") == true) return Ok(physics.Set(viewport, backend, args));
                    if (args["strength"] != null || args["windStrength"] != null) throw new NotSupportedException("Use canonical bounded physics strength/wind overrides for this native renderer.");
                    string mode = args.Value<string>("physicsMode") ?? args.Value<string>("mode");
                    if (mode != "off" && mode != "normal") throw new NotSupportedException("Native physics mode supports off or normal.");
                    var controller = viewport.AvatarRoot.GetComponent<Live2D.Cubism.Framework.Physics.CubismPhysicsController>() ?? throw new NotSupportedException("This model has no authored Cubism physics.");
                    controller.enabled = mode == "normal"; return Ok(PhysicsInfo(args));
                case "vts_postfx_list": return Ok(EffectsInfo(args));
                case "vts_postfx_update": return Ok(UpdateEffects(args));
                case "vts_overlay_info":
                    var effects = Tools().Effects;
                    if (args.Value<bool?>("_vtsCanonical") != true) return Ok(new JObject { ["overlayEnabled"] = effects.overlayStrength > 0, ["colorR"] = effects.overlayColor.r, ["colorG"] = effects.overlayColor.g, ["colorB"] = effects.overlayColor.b, ["colorA"] = effects.overlayStrength });
                    var rgb = Color.Lerp(Color.white, effects.overlayColor, effects.overlayStrength);
                    var part = new JObject { ["active"] = false, ["colorR"] = 255, ["colorG"] = 255, ["colorB"] = 255 };
                    return Ok(new JObject { ["active"] = effects.overlayStrength > 0, ["itemsIncluded"] = false, ["isWindowCapture"] = false, ["baseBrightness"] = 0, ["colorBoost"] = 50, ["smoothing"] = 0,
                        ["colorOverlayR"] = Mathf.RoundToInt(rgb.r * 255), ["colorOverlayG"] = Mathf.RoundToInt(rgb.g * 255), ["colorOverlayB"] = Mathf.RoundToInt(rgb.b * 255), ["colorAvgR"] = Mathf.RoundToInt(rgb.r * 255), ["colorAvgG"] = Mathf.RoundToInt(rgb.g * 255), ["colorAvgB"] = Mathf.RoundToInt(rgb.b * 255), ["leftCapturePart"] = part.DeepClone(), ["middleCapturePart"] = part.DeepClone(), ["rightCapturePart"] = part.DeepClone(), ["sakuraOverlaySource"] = "configured-color" });
                case "vts_items":
                    var stageItems = StageItems();
                    return Ok(new JObject { ["itemsInScene"] = new JArray(stageItems.Items.Select(ItemSnapshot)), ["itemCount"] = stageItems.Items.Count });
                case "vts_item_load": return Ok(await LoadItem(args, cancellation));
                case "vts_item_unload": return Ok(UnloadItem(args));
                case "vts_item_move": return Ok(MoveItem(args));
                case "vts_item_sort": return Ok(SortItems(args));
                case "vts_item_anim": return Ok(AnimateItem(args));
                case "vts_artmesh_select": return Ok(SelectArtMeshes(args));
                case "vts_tint": return Ok(Tint(args));
                case "vts_model_url":
                    var urlState = await LoadModelFromUrl(args, cancellation); Emit("ModelLoadedEvent", urlState); return Ok(urlState);
                case "vts_ndi": return Ok(ConfigureNdi(args));
                default: throw new NotSupportedException("Unsupported native plugin command: " + command);
            }
        }
        SakuraAvatarExpressions.Entry[] MatchExpression(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Choose an expression name.");
            var result = Expressions().Entries.Where(entry => !entry.Id.StartsWith("parameter:", StringComparison.Ordinal) && (entry.Id == name || entry.Name == name || entry.Label == name || entry.Id.StartsWith("expression:", StringComparison.Ordinal) && Path.GetFileName(entry.Id.Substring(11)) == Path.GetFileName(name))).ToArray();
            if (result.Length == 0) throw new ArgumentException("Unknown expression: " + name); return result;
        }
        bool TrackingActive() => viewport != null && viewport.AvatarRoot != null && viewport.AvatarRoot.GetComponent<SakuraTrackingRig>()?.Active == true;
        JObject ParameterTable(JObject args)
        {
            var model = viewport == null || !viewport.HasAvatar ? null : viewport.AvatarRoot.GetComponent<CubismModel>();
            var parameters = new JArray((model == null ? Array.Empty<CubismParameter>() : model.Parameters).Select(parameter => new JObject { ["name"] = parameter.Id, ["value"] = parameter.Value, ["minimum"] = parameter.MinimumValue, ["maximum"] = parameter.MaximumValue, ["defaultValue"] = parameter.DefaultValue }));
            var custom = new JArray();
            foreach (var row in (args["_customParameters"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var copy = (JObject)row.DeepClone(); string name = row.Value<string>("name");
                copy["value"] = name != null && customValues.TryGetValue(name, out float value) ? value : row.Value<float?>("defaultValue") ?? 0; custom.Add(copy);
            }
            return new JObject { ["defaultParameters"] = parameters, ["customParameters"] = custom, ["parameterCount"] = parameters.Count + custom.Count };
        }
        JObject Inject(JObject args)
        {
            var model = Cubism(); var values = args["parameterValues"] as JArray ?? throw new ArgumentException("parameterValues must be an array.");
            if (values.Any(value => !(value is JObject))) throw new ArgumentException("Each injected parameter must be an object.");
            if (injectionModel != viewport.AvatarRoot) ClearInjection(); injectionModel = viewport.AvatarRoot;
            string mode = args.Value<string>("mode") ?? "set";
            if (mode != "set" && mode != "add") throw new ArgumentException("Parameter mode must be set or add.");
            var pending = new List<(CubismParameter parameter, string custom, float value)>();
            foreach (var row in values.OfType<JObject>())
            {
                string name = row.Value<string>("id"); float value = Number(row["value"], "Parameter value"), weight = row["weight"] == null ? 1 : Mathf.Clamp01(Number(row["weight"], "Parameter weight"));
                var parameter = model.Parameters.FirstOrDefault(entry => entry.Id == name);
                var custom = (args["_customParameters"] as JArray ?? new JArray()).OfType<JObject>().FirstOrDefault(entry => entry.Value<string>("name") == name);
                if (parameter == null && custom == null) throw new ArgumentException("Unknown parameter: " + name);
                float current = parameter != null ? injected.TryGetValue(parameter, out float prior) ? prior : parameter.Value : customValues.TryGetValue(name, out float priorCustom) ? priorCustom : custom.Value<float?>("defaultValue") ?? 0;
                float next = mode == "add" ? current + value * weight : Mathf.Lerp(current, value, weight);
                if (parameter != null) next = Mathf.Clamp(next, parameter.MinimumValue, parameter.MaximumValue);
                else next = Mathf.Clamp(next, custom.Value<float>("minimum"), custom.Value<float>("maximum"));
                pending.Add((parameter, name, next));
            }
            var driver = injectionModel.GetComponent<SakuraPluginParameterDriver>() ?? injectionModel.AddComponent<SakuraPluginParameterDriver>(); driver.Owner = this;
            var updates = injectionModel.GetComponent<CubismUpdateController>(); driver.HasUpdateController = updates != null; if (updates != null) updates.Refresh();
            foreach (var row in pending) { if (row.parameter != null) injected[row.parameter] = row.value; else customValues[row.custom] = row.value; }
            injectedUntil = Time.unscaledTime + 10; return new JObject { ["injectedParameterValues"] = pending.Count };
        }
        SakuraStageItems StageItems() => GetComponent<SakuraStageItems>() ?? throw new InvalidOperationException("Stage items are not available in this window.");
        static JObject ItemSnapshot(SakuraStageItems.Item item) => new JObject
        {
            ["itemInstanceID"] = item.id, ["itemID"] = item.id, ["fileName"] = item.source ?? item.name, ["itemType"] = item.kind,
            ["positionX"] = item.x, ["positionY"] = item.y, ["size"] = item.width, ["rotation"] = item.rotation,
            ["layer"] = item.above ? "above" : "below", ["pinned"] = item.follow, ["locked"] = item.locked
        };
        async Task<JObject> LoadItem(JObject args, CancellationToken cancellation)
        {
            var stage = StageItems();
            string name = args.Value<string>("fileName") ?? args.Value<string>("itemFile") ?? args.Value<string>("name") ?? "item";
            string dataUrl = args.Value<string>("customDataBase64") ?? args.Value<string>("dataUrl") ?? "";
            bool above = args.Value<string>("layer") != "below";
            float x = args["positionX"] == null ? .5f : Number(args["positionX"], "positionX");
            float y = args["positionY"] == null ? .5f : Number(args["positionY"], "positionY");
            float size = args["size"] == null ? .25f : Mathf.Clamp(Number(args["size"], "size"), .01f, 3);
            float rotation = args["rotation"] == null ? 0 : Mathf.Clamp(Number(args["rotation"], "rotation"), -180, 180);
            bool follow = args.Value<bool?>("pinned") == true, locked = args.Value<bool?>("locked") == true;
            string id;
            if (dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                int comma = dataUrl.IndexOf(',');
                if (comma < 0) throw new ArgumentException("Invalid image data URL.");
                byte[] bytes;
                try { bytes = Convert.FromBase64String(dataUrl.Substring(comma + 1)); }
                catch (FormatException) { throw new ArgumentException("Invalid base64 image data."); }
                if (bytes.Length > 20 * 1024 * 1024) throw new ArgumentException("Item image must be under 20 MB.");
                id = await stage.AddImage(bytes, name);
            }
            else if (name.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase) || (args.Value<string>("modelPath") ?? "").EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase))
            {
                string path = (args.Value<string>("modelPath") ?? name).Replace('\\', '/').TrimStart('/');
                if (path.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) path = path.Substring(7);
                id = await stage.AddLive2D(path, name);
            }
            else throw new ArgumentException("Send a data:image item or a Live2D .model3.json item. File paths are not read by the API.");
            cancellation.ThrowIfCancellationRequested();
            stage.Configure(id, x, y, size, rotation, above, follow, locked);
            Emit("ItemEvent", new JObject { ["itemEventType"] = "ItemAdded", ["itemInstanceID"] = id });
            return ItemSnapshot(stage.Items.First(item => item.id == id));
        }
        JObject UnloadItem(JObject args)
        {
            var stage = StageItems(); string id = args.Value<string>("itemInstanceID") ?? throw new ArgumentException("itemInstanceID is required.");
            if (!stage.Items.Any(item => item.id == id)) throw new ArgumentException("Unknown itemInstanceID: " + id);
            stage.Remove(id); Emit("ItemEvent", new JObject { ["itemEventType"] = "ItemRemoved", ["itemInstanceID"] = id }); return new JObject { ["itemInstanceID"] = id };
        }
        JObject MoveItem(JObject args)
        {
            var stage = StageItems(); string id = args.Value<string>("itemInstanceID") ?? throw new ArgumentException("itemInstanceID is required.");
            var item = stage.Items.FirstOrDefault(value => value.id == id) ?? throw new ArgumentException("Unknown itemInstanceID: " + id);
            if (args["pinToModelPart"] != null)
            {
                if (args.Value<bool>("pinToModelPart"))
                {
                    string part = args.Value<string>("modelPartName") ?? throw new ArgumentException("modelPartName is required when pinToModelPart is true.");
                    var matches = stage.PinChoices().Where(value => value.Target == part || value.Name == part).ToArray();
                    if (matches.Length != 1) throw new ArgumentException("Unknown or ambiguous model part: " + part);
                    stage.Pin(id, matches[0].Kind, matches[0].Target);
                }
                else stage.Pin(id, null, null);
                return ItemSnapshot(item);
            }
            float x = args["positionX"] == null ? item.x : Number(args["positionX"], "positionX");
            float y = args["positionY"] == null ? item.y : Number(args["positionY"], "positionY");
            float size = args["size"] == null ? item.width : Mathf.Clamp(Number(args["size"], "size"), .03f, 1.5f);
            float rotation = args["rotation"] == null ? item.rotation : Mathf.Clamp(Number(args["rotation"], "rotation"), -180, 180);
            bool above = item.above;
            if (args["layer"]?.Type == JTokenType.String) above = args.Value<string>("layer") == "above";
            if (args["onTop"]?.Type == JTokenType.Boolean) above = args.Value<bool>("onTop");
            bool follow = args["pinned"] == null ? item.follow : args.Value<bool>("pinned");
            bool locked = args["locked"] == null ? item.locked : args.Value<bool>("locked");
            stage.Configure(id, x, y, size, rotation, above, follow, locked);
            return ItemSnapshot(item);
        }
        JObject SortItems(JObject args)
        {
            var stage = StageItems(); var order = new List<(string id, bool? above)>();
            foreach (var entry in (args["items"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string id = entry.Value<string>("itemInstanceID") ?? entry.Value<string>("id"); if (string.IsNullOrEmpty(id)) continue;
                bool? above = null;
                if (entry["onTop"]?.Type == JTokenType.Boolean) above = entry.Value<bool>("onTop");
                else if (entry["layer"]?.Type == JTokenType.String) above = entry.Value<string>("layer") == "above";
                order.Add((id, above));
            }
            stage.Sort(order); return new JObject { ["items"] = new JArray(stage.Items.Select(ItemSnapshot)) };
        }
        JObject AnimateItem(JObject args)
        {
            var stage = StageItems(); string id = args.Value<string>("itemInstanceID") ?? throw new ArgumentException("itemInstanceID is required.");
            if (!stage.Items.Any(item => item.id == id)) throw new ArgumentException("Unknown itemInstanceID: " + id);
            if (args.Value<bool?>("play") == false || args.Value<bool?>("stop") == true) { stage.StopAnimation(id); return new JObject { ["playing"] = false }; }
            var groups = stage.MotionNames(id);
            string group = args.Value<string>("motionGroup") ?? args.Value<string>("animationName") ?? groups.FirstOrDefault();
            if (string.IsNullOrEmpty(group) || !groups.Contains(group)) throw new ArgumentException("Unknown animation. This item has: " + (groups.Count == 0 ? "none" : string.Join(", ", groups)));
            bool loop = args.Value<bool?>("loop") != false; stage.PlayAnimation(id, group, loop);
            return new JObject { ["playing"] = true, ["motionGroup"] = group };
        }
        JObject SelectArtMeshes(JObject args)
        {
            var model = Cubism(); var ids = model.Drawables.Select(drawable => drawable.Id).Where(id => !string.IsNullOrEmpty(id)).ToArray();
            var requested = (args["activeArtMeshes"] as JArray ?? new JArray()).Values<string>().Where(id => id != null && ids.Contains(id)).Distinct().ToList();
            int limit = args["requestedArtMeshCount"] == null ? 0 : Mathf.Clamp((int)Number(args["requestedArtMeshCount"], "requestedArtMeshCount"), 0, ids.Length);
            if (limit > 0) requested = requested.Take(limit).ToList();
            selectedArtMeshes.Clear(); foreach (var id in requested) selectedArtMeshes.Add(id);
            return new JObject { ["activeArtMeshes"] = new JArray(requested) };
        }
        JObject Tint(JObject args)
        {
            var model = Cubism(); var all = model.Drawables.Select(drawable => drawable.Id).Where(id => !string.IsNullOrEmpty(id)).ToList();
            var chosen = selectedArtMeshes.Count > 0 ? selectedArtMeshes.Where(id => all.Contains(id)).ToList() : all;
            float r = args["colorR"] == null ? 1 : Mathf.Clamp01(Number(args["colorR"], "colorR"));
            float g = args["colorG"] == null ? 1 : Mathf.Clamp01(Number(args["colorG"], "colorG"));
            float b = args["colorB"] == null ? 1 : Mathf.Clamp01(Number(args["colorB"], "colorB"));
            float a = args["colorA"] == null ? 1 : Mathf.Clamp01(Number(args["colorA"], "colorA"));
            var tools = viewport.AvatarRoot.GetComponent<SakuraModelTools>() ?? throw new InvalidOperationException("Color tint is unavailable for this model.");
            int painted = tools.TintArtMeshes(chosen, new Color(r, g, b, a));
            return new JObject { ["artMeshCount"] = painted, ["colorTint"] = new JObject { ["colorR"] = r, ["colorG"] = g, ["colorB"] = b, ["colorA"] = a } };
        }
        async Task<JObject> LoadModelFromUrl(JObject args, CancellationToken cancellation)
        {
            string url = args.Value<string>("url") ?? args.Value<string>("modelURL") ?? throw new ArgumentException("url is required.");
            if (url.Length > 4096) throw new ArgumentException("Model URL is too long.");
            string relative;
            if (url.StartsWith("/models/", StringComparison.OrdinalIgnoreCase)) relative = Uri.UnescapeDataString(url.Substring(8));
            else if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile) relative = Path.GetRelativePath(backend.SourceFolder, uri.LocalPath);
            else throw new NotSupportedException("Only local /models paths or file:// URLs are supported by the native Sakura window.");
            relative = relative.Replace('\\', '/').TrimStart('/');
            string full = SakuraModelImport.ResolveFile(backend.SourceFolder, relative);
            if (!File.Exists(full)) throw new FileNotFoundException("The model file does not exist.", full);
            await viewport.LoadModel(backend.SourceFolder, relative, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!viewport.HasAvatar || !string.IsNullOrEmpty(viewport.LastImportError)) throw new InvalidOperationException(viewport.LastImportError ?? "The model could not be loaded.");
            return State();
        }
        JObject ConfigureNdi(JObject args)
        {
            var output = UnityEngine.Object.FindFirstObjectByType<SakuraOutput>() ?? throw new InvalidOperationException("The output service is unavailable.");
            var channel = output.Channels.FirstOrDefault(value => value.Id == "ndi") ?? throw new InvalidOperationException("NDI output is unavailable.");
            bool active = channel.Enabled;
            if (args.Value<bool?>("setNewConfig") == true)
            {
                if (args.Value<bool?>("useNDI5") == false) throw new NotSupportedException("Switching NDI runtime versions is unavailable.");
                if (args.Value<bool?>("useCustomResolution") == true) throw new NotSupportedException("Custom NDI resolutions are unavailable in this window.");
                if (args["ndiActive"] != null) { bool want = args.Value<bool>("ndiActive"); if (want != channel.Enabled) { output.Configure("ndi", want); active = want; } }
            }
            return new JObject { ["ndiActive"] = active, ["useNDI5"] = true, ["useCustomResolution"] = false, ["customWidthNDI"] = 1920, ["customHeightNDI"] = 1080 };
        }
        void RestoreWrites()
        {
            foreach (var row in writes) if (row.Key != null && Mathf.Abs(row.Key.Value - row.Value.after) < .00001f) row.Key.Value = row.Value.before;
            writes.Clear();
        }
        void ClearInjection() { RestoreWrites(); injected.Clear(); customValues.Clear(); injectionModel = null; }
        public void OnLateUpdate()
        {
            if (!isActiveAndEnabled) return;
            if (viewport == null || viewport.AvatarRoot != injectionModel) { ClearInjection(); return; }
            if (Time.unscaledTime >= injectedUntil) { RestoreWrites(); injected.Clear(); customValues.Clear(); return; }
            foreach (var row in injected) if (row.Key != null) { float before = row.Key.Value; row.Key.Value = row.Value; writes[row.Key] = (before, row.Value); }
        }
        JObject PhysicsInfo(JObject args)
        {
            bool loaded = viewport != null && viewport.HasAvatar;
            var tools = loaded ? viewport.AvatarRoot.GetComponent<SakuraModelTools>() : null;
            var controller = loaded ? viewport.AvatarRoot.GetComponent<Live2D.Cubism.Framework.Physics.CubismPhysicsController>() : null;
            if (args.Value<bool?>("_vtsCanonical") == true)
            {
                var state = State(); state["modelHasPhysics"] = tools?.HasLive2DPhysics == true; state["physicsSwitchedOn"] = controller != null && controller.enabled;
                state["usingLegacyPhysics"] = tools?.Live2D.legacy == true; state["physicsFPSSetting"] = tools == null || tools.Live2D.physicsFps == 0 ? -1 : tools.Live2D.physicsFps;
                state["baseStrength"] = 50; state["baseWind"] = 0; state["apiPhysicsOverrideActive"] = false; state["apiPhysicsOverridePluginName"] = ""; state["physicsGroups"] = new JArray();
                if (tools?.HasLive2DPhysics == true) foreach (var property in physics.Query(viewport, backend).Properties()) state[property.Name] = property.Value;
                return state;
            }
            return new JObject { ["physicsEnabled"] = controller != null && controller.enabled, ["physicsMode"] = controller != null && controller.enabled ? "normal" : "off", ["strength"] = 1, ["windStrength"] = 0, ["engine"] = tools?.HasLive2DPhysics == true ? "cubism" : "unavailable", ["chains"] = 0, ["bones"] = 0 };
        }
        JObject EffectsInfo(JObject args)
        {
            var settings = Tools().Effects;
            if (args.Value<bool?>("_vtsCanonical") != true) return new JObject { ["postProcessing"] = new JArray(new JObject { ["name"] = "glow", ["value"] = settings.glow }, new JObject { ["name"] = "saturation", ["value"] = settings.saturation }, new JObject { ["name"] = "contrast", ["value"] = settings.contrast }) };
            JObject Config(string id, float value, float min, float max, float fallback, bool activation) => new JObject { ["internalID"] = id, ["enumID"] = id, ["explanation"] = id, ["type"] = "Float", ["activationConfig"] = activation, ["floatValue"] = value, ["floatMin"] = min, ["floatMax"] = max, ["floatDefault"] = fallback, ["intValue"] = 0, ["intMin"] = 0, ["intMax"] = 0, ["intDefault"] = 0, ["boolValue"] = false, ["boolDefault"] = false, ["colorValue"] = "", ["colorDefault"] = "", ["colorHasAlpha"] = false, ["stringValue"] = "", ["stringDefault"] = "", ["sceneItemValue"] = "", ["sceneItemDefault"] = "" };
            JObject Effect(string id, bool active, params JObject[] config) => new JObject { ["internalID"] = id, ["enumID"] = id, ["explanation"] = id, ["effectIsActive"] = active, ["effectIsRestricted"] = false, ["configEntries"] = new JArray(config) };
            var effects = new JArray(Effect("Bloom", settings.glow > 0, Config("Bloom_Strength", settings.glow, 0, 1, 0, true)), Effect("ColorGrading", settings.saturation != 1 || settings.contrast != 1 || settings.overlayStrength > 0, Config("ColorGrading_Saturation", (settings.saturation - 1) * 100, -100, 100, 0, false), Config("ColorGrading_Contrast", (settings.contrast - 1) * 100, -100, 100, 0, false)));
            if (args["effectIDFilter"] is JArray filter && filter.Count > 0) effects = new JArray(effects.Where(effect => filter.Values<string>().Contains(effect.Value<string>("enumID"))));
            return new JObject { ["postProcessingSupported"] = true, ["postProcessingActive"] = true, ["canSendPostProcessingUpdateRequestRightNow"] = true, ["restrictedEffectsAllowed"] = false, ["presetIsActive"] = false, ["activePreset"] = "", ["presetCount"] = 0, ["activeEffectCount"] = effects.Count(effect => effect.Value<bool>("effectIsActive")), ["effectCountBeforeFilter"] = 2, ["configCountBeforeFilter"] = 3, ["effectCountAfterFilter"] = effects.Count, ["configCountAfterFilter"] = effects.Sum(effect => ((JArray)effect["configEntries"]).Count), ["postProcessingEffects"] = args.Value<bool?>("fillPostProcessingEffectsArray") == false ? new JArray() : effects, ["postProcessingPresets"] = new JArray() };
        }
        JObject UpdateEffects(JObject args)
        {
            var tools = Tools(); var current = tools.Effects;
            var next = new SakuraModelTools.EffectSettings { glow = current.glow, saturation = current.saturation, contrast = current.contrast, overlayColor = current.overlayColor, overlayStrength = current.overlayStrength };
            bool canonical = args.Value<bool?>("_vtsCanonical") == true;
            if (canonical)
            {
                if (args.Value<bool?>("setPostProcessingPreset") == true || args.Value<bool?>("randomizeAll") == true || args["postProcessingFadeTime"] != null && Number(args["postProcessingFadeTime"], "Fade time") != 0 || args.Value<bool?>("postProcessingOn") == false) throw new NotSupportedException("Native effect presets, randomization, fades and global post-processing toggles are unavailable. Set supported config values directly.");
                if (args.Value<bool?>("setPostProcessingValues") != true) return EffectsInfo(args);
                if (args.Value<bool?>("setAllOtherValuesToDefault") == true) next = new SakuraModelTools.EffectSettings();
                var entries = args["postProcessingValues"] as JArray ?? throw new ArgumentException("postProcessingValues must be an array.");
                if (entries.Count > 3 || entries.Any(entry => !(entry is JObject))) throw new ArgumentException("Use at most three supported native effect config objects.");
                var seen = new HashSet<string>();
                foreach (var row in entries.OfType<JObject>())
                {
                    string id = row.Value<string>("configID"); string text = row.Value<string>("configValue");
                    if (id == null || !seen.Add(id)) throw new ArgumentException("Effect config IDs must be unique.");
                    if (!float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value)) throw new ArgumentException("Effect value must be finite numeric text.");
                    if (id == "Bloom_Strength") next.glow = Mathf.Clamp01(value);
                    else if (id == "ColorGrading_Saturation") next.saturation = 1 + Mathf.Clamp(value, -100, 100) / 100;
                    else if (id == "ColorGrading_Contrast") next.contrast = 1 + Mathf.Clamp(value, -100, 100) / 100;
                    else throw new NotSupportedException("Unsupported native effect config: " + id);
                }
            }
            else
            {
                var entries = args["postProcessing"] as JArray ?? throw new ArgumentException("postProcessing must be an array.");
                if (entries.Any(entry => !(entry is JObject))) throw new ArgumentException("Each post-processing entry must be an object.");
                foreach (var row in entries.OfType<JObject>())
                {
                    string name = row.Value<string>("name"); float value = Number(row["value"], "Effect value");
                    if (name == "glow") next.glow = Mathf.Clamp01(value); else if (name == "saturation") next.saturation = Mathf.Clamp(value, 0, 2); else if (name == "contrast") next.contrast = Mathf.Clamp(value, 0, 2); else throw new NotSupportedException("Unsupported native effect: " + name);
                }
            }
            tools.SetEffects(next); return EffectsInfo(args);
        }

        public void Emit(string name, JObject data = null)
        {
            if (!Connected || string.IsNullOrEmpty(name)) return;
            _ = SendEvent(new JObject { ["type"] = "event", ["event"] = name, ["data"] = data ?? new JObject() });
        }
        async Task SendEvent(JObject message)
        {
            try
            {
                await sendLock.WaitAsync();
                try { var ws = socket; if (ws != null && ws.State == WebSocketState.Open) { var bytes = Encoding.UTF8.GetBytes(message.ToString(Formatting.None)); await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); } }
                finally { sendLock.Release(); }
            }
            catch (Exception) { }
        }
        public void Configure(bool enabled, int requestedPort)
        {
            if (backend == null) throw new InvalidOperationException("Initialize the native plugin bridge first.");
            if (requestedPort < 1024 || requestedPort > 65535) throw new ArgumentException("Use a plugin port between 1024 and 65535.");
            desiredEnabled = enabled; desiredPort = requestedPort; configurationRevision++;
            if (!enabled) StopBridge();
            if (!configuring) ApplyConfiguration();
        }
        void ApplyConfiguration()
        {
            bool enabled = desiredEnabled; int requestedPort = desiredPort; configuring = true;
            Status = enabled ? "Starting the authenticated external plugin listener…" : "Stopping plugin listener…";
            backend.Api("/api/plugins/configure", new JObject { ["enabled"] = enabled, ["port"] = requestedPort }.ToString(Formatting.None), (_, error) =>
            {
                if (this == null) return;
                configuring = false;
                if (error != null) Status = error;
                else
                {
                    externalEnabled = enabled; port = requestedPort;
                    StopBridge(); Status = enabled ? "Plugin listener enabled. Connecting the native renderer…" : "Plugin server is off.";
                }
                if (desiredEnabled != enabled || desiredPort != requestedPort) ApplyConfiguration();
            });
        }
        void Update()
        {
            RestoreWrites();
            if (injectionModel != null && (viewport == null || viewport.AvatarRoot != injectionModel)) ClearInjection();
            physics.Tick(viewport);
            for (int index = 0; index < 4 && main.TryDequeue(out var action); index++) action();
            if (externalEnabled && desiredEnabled && backend != null && backend.Connected && !running && Time.realtimeSinceStartupAsDouble >= retryAfter) _ = Session();
            if (Connected) { bool face = GetComponent<SakuraTracking>()?.FaceDetected == true; if (!faceKnown || face != lastFace) { faceKnown = true; lastFace = face; Emit("TrackingStatusChangedEvent", new JObject { ["faceFound"] = face }); } }
            else faceKnown = false;
        }
        async Task Session()
        {
            running = true; int version = ++generation; lifetime = new CancellationTokenSource(); var cancel = lifetime.Token;
            try
            {
                socket = backend.CreatePluginBridgeSocket(out Uri endpoint); await socket.ConnectAsync(endpoint, cancel); Status = "Native plugin renderer connected · external port " + port + ".";
                var buffer = new byte[8192];
                while (!cancel.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    using (var bytes = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancel);
                            if (result.MessageType == WebSocketMessageType.Close) { Status = "Native plugin bridge closed. Retrying while the listener is enabled."; return; }
                            if (result.MessageType != WebSocketMessageType.Text || bytes.Length + result.Count > MaximumMessage) throw new InvalidDataException("Plugin bridge received an oversized or non-text command.");
                            bytes.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);
                        JObject message;
                        using (var text = new StringReader(new UTF8Encoding(false, true).GetString(bytes.ToArray())))
                        using (var json = new JsonTextReader(text) { MaxDepth = 16 }) message = JObject.Load(json);
                        var answer = new TaskCompletionSource<JObject>();
                        using (cancel.Register(() => answer.TrySetCanceled()))
                        {
                            main.Enqueue(async () =>
                            {
                                if (cancel.IsCancellationRequested || version != generation || !isActiveAndEnabled || !externalEnabled || !desiredEnabled) { answer.TrySetCanceled(); return; }
                                try
                                {
                                    var response = await Dispatch(message, cancel);
                                    if (cancel.IsCancellationRequested || version != generation) answer.TrySetCanceled(); else answer.TrySetResult(response);
                                }
                                catch (Exception error) { answer.TrySetException(error); }
                            });
                            var response = await answer.Task; byte[] payload = Encoding.UTF8.GetBytes(response.ToString(Formatting.None));
                            if (payload.Length > 512 * 1024) payload = Encoding.UTF8.GetBytes(new JObject { ["id"] = message.Value<string>("id"), ["ok"] = false, ["error"] = "Native plugin response exceeds 512 KB." }.ToString(Formatting.None));
                            await sendLock.WaitAsync(cancel);
                    try { await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, cancel); }
                    finally { sendLock.Release(); }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (version == generation) Status = "Native plugin bridge disconnected. " + error.Message; }
            finally
            {
                if (version == generation) { socket?.Dispose(); socket = null; running = false; retryAfter = Time.realtimeSinceStartupAsDouble + 3; lifetime?.Dispose(); lifetime = null; }
            }
        }
        void StopBridge()
        {
            generation++; lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; socket?.Abort(); socket?.Dispose(); socket = null; running = false; ClearInjection(); physics.Clear();
            Status = "Native plugin renderer disconnected.";
            while (main.TryDequeue(out _)) { }
        }
        public void BuildPanel(VisualElement parent)
        {
            var owner = new VisualElement(); parent.Add(owner); owner.Add(new Label("Plugins & the VTube Studio API"));
            var enabled = new Toggle("Accept plugin commands") { value = externalEnabled }; owner.Add(enabled);
            var number = new IntegerField("Plugin port") { value = port, isDelayed = true }; owner.Add(number);
            var status = new Label(Status); status.AddToClassList("description"); owner.Add(status);
            owner.Add(new Label("External plugins use the existing server’s token approval. Sakura-key clients use the existing plugin key."));
            enabled.RegisterValueChangedCallback(_ => { try { Configure(enabled.value, number.value); } catch (Exception error) { Status = error.Message; enabled.SetValueWithoutNotify(externalEnabled); } });
            number.RegisterValueChangedCallback(_ => { if (externalEnabled) try { Configure(true, number.value); } catch (Exception error) { Status = error.Message; } });
            int statusRevision = configurationRevision;
            backend.Api("/api/plugins/status", null, (json, error) =>
            {
                if (this == null || error != null) { if (error != null) Status = error; return; }
                if (statusRevision != configurationRevision || configuring) return;
                var reply = JObject.Parse(json); externalEnabled = reply.Value<bool>("enabled"); int currentPort = reply.Value<int>("port"); if (currentPort >= 1024) port = currentPort;
                desiredEnabled = externalEnabled; desiredPort = port;
                enabled.SetValueWithoutNotify(externalEnabled); number.SetValueWithoutNotify(port);
            });
            var poll = status.schedule.Execute(() => { if (this != null) { status.text = Status; enabled.SetValueWithoutNotify(configuring ? desiredEnabled : externalEnabled); } }).Every(500);
            owner.RegisterCallback<DetachFromPanelEvent>(_ => poll.Pause());
        }
        void OnDisable() => StopBridge();
        void OnDestroy() { StopBridge(); if (backend != null) backend.CatalogReceived -= Catalog; }
    }
    public sealed class SakuraPluginParameterDriver : MonoBehaviour, ICubismUpdatable
    {
        public SakuraPluginBridge Owner;
        public bool HasUpdateController { get; set; }
        public int ExecutionOrder => 980;
        public bool NeedsUpdateOnEditing => false;
        public void OnLateUpdate() { if (isActiveAndEnabled && Owner != null) Owner.OnLateUpdate(); }
        void LateUpdate() { if (!HasUpdateController) OnLateUpdate(); }
    }
}
