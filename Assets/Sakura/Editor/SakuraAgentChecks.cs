using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity.Editor
{
    public static class SakuraAgentChecks
    {
        static Task<JToken> Call(SakuraBackend backend,string route,JObject body=null,string method=null)
        {
            var done=new TaskCompletionSource<JToken>();
            void Complete(string json,string error)
            {
                if(error!=null)done.TrySetException(new InvalidOperationException(error));
                else {try{done.TrySetResult(JToken.Parse(json));}catch(Exception e){done.TrySetException(e);}}
            }
            if(route.StartsWith("/api/agent/"))backend.AgentApi(route,body?.ToString(),Complete,method);
            else backend.Api(route,body?.ToString(),Complete);
            return done.Task;
        }
        public static void Validate()
        {
            foreach(string route in new[]{"https://example.com/api/agent/state","/api/agent/../../settings","/api/agent/state#fragment","/api/agent/%2e%2e/settings"})
                if(SakuraBackend.ValidAgentRoute(route))throw new Exception("Unsafe Agent route accepted");
            if(!SakuraBackend.ValidAgentRoute("/api/agent/history/search?q=hello%20world"))throw new Exception("Search route rejected");
            string token=new string('a',54);
            if(SakuraBackend.ReadAgentToken("const token='"+token+"';")!=token)throw new Exception("Session authorization parse failed");
            bool refused=false;try{SakuraBackend.ReadAgentToken("const token='__AGENT_TOKEN__';");}catch(InvalidOperationException){refused=true;}
            if(!refused)throw new Exception("Uninitialized Agent page accepted");
            if(SakuraBackend.ResponseError("{\"detail\":\"Teacher mode required\"}","409")!="Teacher mode required")throw new Exception("Agent rejection detail lost");
            Debug.Log("PASS: native Agent route boundaries, session authorization and API rejection messages.");
        }
        public static async Task VerifyLive()
        {
            Validate();
            var studio=UnityEngine.Object.FindFirstObjectByType<SakuraStudio>();
            if(studio==null||!Application.isPlaying)throw new Exception("Open the Studio in Play mode");
            studio.SwitchMode("Agent");
            var doc=studio.GetComponent<UIDocument>();
            if(doc.rootVisualElement.Q("agent-workspace")==null)throw new Exception("Native Agent workspace did not open");
            var backend=studio.GetComponent<SakuraBackend>();
            var state=await Call(backend,"/api/agent/state");
            if(state["runs"] is not JArray runs || state["providers"] is not JArray)throw new Exception("Agent state missing");
            if(runs.Count>=29)throw new Exception("Owner task history is full; test task not created to avoid eviction");
            var settings=await Call(backend,"/api/settings");
            string previous=(string)settings["conversation_mode"]??"friend",id=null;
            try
            {
                await Call(backend,"/api/settings",new JObject {["conversation_mode"]="teacher"});
                string folder=@"C:\AI\Unity-Verification\agent-test-workspace";Directory.CreateDirectory(folder);
                var task=await Call(backend,"/api/agent/tasks",new JObject {["goal"]="Native migration verification: reject a directory listing without executing it.",["workspace"]=folder,["auto_continue"]=false,["approval_mode"]="ask"});
                id=(string)task["id"];
                var proposed=await Call(backend,"/api/agent/tasks/"+id+"/propose",new JObject {["tool"]="files.list",["arguments"]=new JObject {["path"]="."},["reason"]="Disposable native integration test; reject without execution."});
                var step=proposed["steps"]?.Last;
                if((string)step?["status"]!="pending")throw new Exception("Action did not wait for approval");
                var result=await Call(backend,"/api/agent/actions/"+(string)step["id"]+"/decision",new JObject {["approve"]=false});
                if((string)result["action"]?["status"]!="rejected")throw new Exception("Rejection not recorded");
                await Call(backend,"/api/agent/tasks/"+id+"/cancel",new JObject());
                File.WriteAllText(@"C:\AI\Unity-Verification\native-agent-check.json","{\"authorized\":true,\"taskCreated\":true,\"actionRejected\":true,\"ownerTasksPreserved\":true}");
            }
            finally
            {
                if(id!=null)await Call(backend,"/api/agent/tasks/"+id,null,"DELETE");
                await Call(backend,"/api/settings",new JObject {["conversation_mode"]=previous});
            }
            studio.SwitchMode("Companion");studio.SwitchMode("Agent");
            await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\native-agent.png");
            Debug.Log("PASS: native Agent authorization, task create/inspect, proposed action rejection, cancellation, deletion; owner conversation preference restored.");
        }
    }
}
