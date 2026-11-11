using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TAS.Common;
using TAS.Common.Interfaces;

namespace TAS.Client.VPlayMigration
{
    public sealed partial class Plugin
    {
        private readonly Dictionary<int,IEvent> _boundedReferences=new Dictionary<int,IEvent>();
        private readonly HashSet<int> _boundedApplied=new HashSet<int>();
        internal static bool FileOverrideSourceAllowed(JObject s,JObject a,long beginTicks)
        {
            if((bool?)a["Approved"]!=true||(int?)a["Sequence"]!=(int?)s["Sequence"]||(string)a["ItemId"]!=(string)s["ItemId"]||
                (string)a["AssetKey"]!=(string)s["AssetKey"]||(string)a["XmlSha256"]!=(string)s["XmlSha256"]||(long?)a["BeginUtcTicks"]!=beginTicks||
                (long?)a["DurationTicks"]!=(long?)s["DurationTicks"])return false;
            long duration=(long?)s["DurationTicks"]??0;
            if(duration<=0||duration>TimeSpan.FromMinutes(3).Ticks||duration%400000!=0||(string)s["Kind"]!="File"||(string)s["ItemType"]!="1"||
                (string)s["EventType"]!="2"||(long?)s["TimeCodeInTicks"]!=0||(long?)s["StartTimecodeTicks"]!=0||
                (long?)s["OutOffsetTicks"]!=duration||((long?)s["ClipDurationTicks"]??-1)<0||(long?)s["ClipDurationTicks"]<duration||(long?)s["ClipDurationTicks"]>duration+400000||
                (string)s["OriginalAttributes"]?["LogoIndex"]!="0")return false;
            var issues=s["Issues"] as JArray;
            if(issues==null||issues.Count!=1||(string)issues[0]!="EventType requires semantics review")return false;
            var xml=new System.Xml.XmlDocument();try{xml.LoadXml((string)s["AudioXml"]??"");}catch(System.Xml.XmlException){return false;}
            if(xml.DocumentElement?.Name!="AudioConfig")return false;
            foreach(System.Xml.XmlAttribute attr in xml.DocumentElement.Attributes)if(attr.Value!="1")return false;
            return xml.DocumentElement.GetAttribute("Matrix_0")=="1"&&xml.DocumentElement.GetAttribute("Matrix_1")=="1"&&
                xml.DocumentElement.GetAttribute("Gain_0")=="1"&&xml.DocumentElement.GetAttribute("Gain_1")=="1";
        }
        private IEvent ResolveFileOverride(SourceSlot slot)
        {
            try{
                var path=Path.Combine(Home,"playout-overrides.json");if(!File.Exists(path))return null;
                var approvals=JArray.Parse(File.ReadAllText(path));var a=approvals.Cast<JObject>().SingleOrDefault(x=>(int?)x["Sequence"]==(int?)slot.Source["Sequence"]);
                if(a==null||!FileOverrideSourceAllowed(slot.Source,a,slot.Begin.Ticks))return null;
                Guid guid,jobId;if(!Guid.TryParse((string)a["MediaGuid"],out guid)||guid==Guid.Empty||!Guid.TryParse((string)a["NativeJobId"],out jobId)||jobId==Guid.Empty)return null;
                if(!System.Text.RegularExpressions.Regex.IsMatch((string)a["ResultHash"]??"","^[A-Fa-f0-9]{64}$")||
                    !System.Text.RegularExpressions.Regex.IsMatch((string)a["InputHash"]??"","^[A-Fa-f0-9]{64}$")||(long?)a["Bytes"]<=0||a["Bytes"]==null)return null;
                var engine=Engine as TAS.Server.Engine;if(engine==null||engine.IncomingIngestProfileName!="EWTN Managed Incoming"||!engine.AutoArchiveAfterIngest)return null;
                var job=JArray.Parse(engine.IncomingJournal).Cast<JObject>().SingleOrDefault(j=>(string)j["Id"]==jobId.ToString());
                if(job==null||(string)job["State"]!="Done"||(string)job["Target"]!=guid.ToString()||(string)job["CandidateHash"]!=(string)a["ResultHash"]||(string)job["InputHash"]!=(string)a["InputHash"])return null;
                var media=Engine.MediaManager.MediaDirectoryPRI.GetAllFiles().SingleOrDefault(m=>m.MediaGuid==guid);
                if(media==null||media.MediaStatus!=TMediaStatus.Available||!media.FileExists()||media.MediaName!=(string)a["LogicalName"]||
                    media.VideoFormat!=TVideoFormat.HD1080i5000||media.TcPlay!=TimeSpan.Zero||media.DurationPlay<slot.End-slot.Begin||
                    media.DurationPlay>slot.End-slot.Begin+TimeSpan.FromMilliseconds(40))return null;
                TAS.Server.PlayoutCacheRules.Request(media);if(!Ready(media))return null;
                var cached=JArray.Parse(File.ReadAllText(Path.Combine(Home,"cache-index.json"))).Cast<JObject>().SingleOrDefault(c=>(string)c["Guid"]==guid.ToString());
                if(cached==null||(string)cached["Hash"]!=(string)a["ResultHash"]||(long?)cached["Bytes"]!=(long?)a["Bytes"]||(ulong)(long)a["Bytes"]!=media.FileSize)return null;
                int sequence=(int)slot.Source["Sequence"];IEvent reference;
                if(!_boundedReferences.TryGetValue(sequence,out reference)){
                    string key="VP26O:"+((string)slot.Source["XmlSha256"]).Substring(0,8)+":"+sequence;
                    reference=Events().SingleOrDefault(e=>e.IdAux==key);
                    if(reference==null){
                        var root=Events().Single(e=>e.IdAux=="VP26PREPARATION");
                        reference=Engine.CreateNewEvent(videoLayer:VideoLayer.Program,eventType:TEventType.Movie,startType:TStartType.Manual,scheduledTime:slot.Begin,
                            duration:slot.End-slot.Begin,mediaGuid:guid,eventName:"Bounded file-only override "+sequence,idAux:key);
                        root.InsertUnder(reference,false);reference.IsEnabled=true;reference.Save();
                    }
                    _boundedReferences[sequence]=reference;
                }
                if(reference.MediaGuid!=guid||reference.Duration!=slot.End-slot.Begin||reference.ScheduledTime!=slot.Begin||reference.ScheduledTc!=TimeSpan.Zero)return null;
                return reference;
            }catch(Exception ex){Save("playout-override-error.json",new{Utc=DateTime.UtcNow,Error=ex.Message});return null;}
        }
        private void RecordFileOverride(SourceSlot slot,IEvent reference)
        {
            int sequence=(int)slot.Source["Sequence"];if(!_boundedApplied.Add(sequence))return;
            File.AppendAllText(Path.Combine(Home,"playout-overrides-applied.jsonl"),new JObject{
                ["Utc"]=DateTime.UtcNow,["Sequence"]=sequence,["BeginUtcTicks"]=slot.Begin.Ticks,["EndUtcTicks"]=slot.End.Ticks,["MediaGuid"]=reference.MediaGuid.ToString(),
                ["ReferenceId"]=reference.Id,["Reason"]="Bounded file-only EventType2 fallback; native BRv1/full QC/Archive/C-cache proof; original event remains review"}.ToString(Newtonsoft.Json.Formatting.None)+Environment.NewLine);
        }
    }
}
