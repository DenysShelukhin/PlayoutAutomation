// EWTN local absolute-time schedule. No source dates or media identity are rewritten.
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TAS.Common;
using TAS.Common.Interfaces;
namespace TAS.Client.VPlayMigration
{
    public sealed partial class Plugin
    {
        private readonly DispatcherTimer _absoluteTimer;
        private sealed class SourceSlot { internal JObject Source;internal IEvent Event;internal DateTime Begin,End; }
        private SourceSlot[] _absoluteSources;
        private IEvent _diagnosticFiller;
        private string _absoluteState;
        private DateTime _absoluteStatusAt;
        private void AbsoluteTick()
        {
            if(!File.Exists(Path.Combine(Home,"absolute-enabled.json")))return;
            try {
                SafeHome();var engine=Engine as TAS.Server.Engine;if(engine==null)throw new IOException("Local engine required");
                var enabled=JObject.Parse(File.ReadAllText(Path.Combine(Home,"absolute-enabled.json")));
                if((bool?)enabled["Enabled"]!=true){engine.AbsoluteScheduleEnabled=false;return;}
                if(_absoluteSources==null){
                    var native=Events().Where(e=>(e.IdAux??"").StartsWith("VP26E:")).ToDictionary(e=>e.IdAux);
                    var saved=JArray.Parse(File.ReadAllText(Path.Combine(Home,"native-event-map.json"))).Cast<JObject>().ToDictionary(s=>(string)s["IdAux"]);
                    _absoluteSources=JArray.Parse(File.ReadAllText(Path.Combine(Home,"events.json"))).Cast<JObject>().Select(s=>{
                        var key="VP26E:"+((string)s["ItemId"]).Replace("-","");
                        if(!saved.TryGetValue(key,out var identity))throw new IOException("Verified native source identity missing: "+key);
                        if(!native.TryGetValue(key,out var e))e=TAS.Server.DatabaseProvider.Database.ReadEvent(Engine,(ulong)identity["Id"]);
                        if(e==null||e.IdAux!=key||e.Duration.Ticks!=(long)s["DurationTicks"])throw new IOException("Native source identity mismatch: "+key);
                        var begin=new DateTime((long)identity["ScheduledTicks"],DateTimeKind.Utc); // exact verified native/source ticks
                        return new SourceSlot{Source=s,Event=e,Begin=begin,End=begin.AddTicks((long)s["DurationTicks"])};
                    }).OrderBy(s=>s.Begin).ToArray();
                    _diagnosticFiller=Events().FirstOrDefault(e=>(e.IdAux??"").StartsWith("VP26T:")&&e.EventType==TEventType.Movie&&e.EventName.StartsWith("ID_",StringComparison.OrdinalIgnoreCase)&&Ready(e.Media));
                    if(_diagnosticFiller!=null){_diagnosticFiller.IsLoop=true;_diagnosticFiller.IsEnabled=true;_diagnosticFiller.Save();}
                }
                var now=engine.CurrentTime;var slot=_absoluteSources.FirstOrDefault(s=>s.Begin<=now&&s.End>now);
                engine.AbsoluteScheduleEnabled=true;
                string mode,reason;int sequence=0;double offset=0;
                if(slot==null){mode="Outside XML dates / waiting";reason="No historical event is started as current";}
                else {
                    sequence=(int)slot.Source["Sequence"];offset=(now-slot.Begin).TotalSeconds;
                    var approved=ResolveFileOverride(slot);var playable=approved??slot.Event;
                    var supported=approved!=null||((string)slot.Source["ParseStatus"]=="Pending"&&slot.Event.EventType==TEventType.Movie);
                    if(supported&&playable.MediaGuid==Guid.Empty){
                        var catalog=JArray.Parse(File.ReadAllText(Path.Combine(Home,"catalog.json")));
                        var asset=catalog.FirstOrDefault(a=>(string)a["Key"]==(string)slot.Source["AssetKey"]);
                        var media=Engine.MediaManager.MediaDirectoryPRI.GetAllFiles().Where(m=>m.MediaName==(string)asset?["LogicalName"]).ToArray();
                        if(media.Length==1){playable.MediaGuid=media[0].MediaGuid;playable.Save();}
                    }
                    if(supported&&playable.Media!=null)TAS.Server.PlayoutCacheRules.Request(playable.Media);
                    if(supported&&Ready(playable.Media)){
                        mode="Scheduled media";reason=approved==null?"Original UTC anchor; native frame offset":"Bounded file-only override; full native technical proof";
                        if(engine.Playing!=playable&&slot.End-now>TimeSpan.FromMilliseconds(250))engine.StartAbsolute(playable,slot.Begin);
                        if(approved!=null&&engine.Playing==playable)RecordFileOverride(slot,playable);
                        if(engine.Playing==playable)CheckPhysicalClock(playable,slot.Begin,slot.End);
                    } else {
                        mode="Diagnostic filler";reason=supported?"Missing / Not Ready":"Special Event / Requires Review";
                        if(_diagnosticFiller!=null&&Ready(_diagnosticFiller.Media)){
                            if(engine.Playing!=_diagnosticFiller)engine.StartAbsolute(_diagnosticFiller,now);
                        }else if(engine.Playing!=null)engine.Clear(VideoLayer.Program);
                    }
                }
                // Consume the current prepared producer before preparing its
                // successor. A short (<30s) current slot must not lose its own
                // warm background to next-slot preparation at the same tick.
                var upcoming=_absoluteSources.FirstOrDefault(s=>s.Begin>now);
                if(upcoming!=null&&upcoming.Begin-now<TimeSpan.FromSeconds(30)){
                    var next=(string)upcoming.Source["ParseStatus"]=="Pending"?upcoming.Event:ResolveFileOverride(upcoming);
                    if(next!=null&&next.EventType==TEventType.Movie&&Ready(next.Media))engine.PrepareAbsolute(next);
                }
                var state=sequence+":"+mode;
                var report=new{Utc=DateTime.UtcNow,Mode=mode,Reason=reason,Sequence=sequence,SourceBeginUtc=slot?.Begin,SourceEndUtc=slot?.End,ExpectedOffsetSeconds=offset,PlayingId=engine.Playing?.Id,NativePlayingPosition=(engine.Playing as TAS.Server.Event)?.Position,OriginalDates=true,TimeZone="Europe/Kyiv"};
                if(state!=_absoluteState){File.AppendAllText(Path.Combine(Home,"absolute-asrun.jsonl"),JsonConvert.SerializeObject(report)+Environment.NewLine);_absoluteState=state;}
                if(now-_absoluteStatusAt>=TimeSpan.FromSeconds(1)){Save("absolute-status.json",report);_absoluteStatusAt=now;}
            }catch(Exception e){Save("absolute-error.json",new{Utc=DateTime.UtcNow,Error=e.ToString()});}
        }
    }
}
