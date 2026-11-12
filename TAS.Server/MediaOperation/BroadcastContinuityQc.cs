// AIR-02 only. Explicit owner-approved experimental continuity policy; default Strict.
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace TAS.Server.MediaOperation
{
    internal static class BroadcastContinuityQc
    {
        internal static readonly string Home=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"EWTN","Playout","Migration");
        [DataContract] private sealed class Policy
        {
            [DataMember] public int Version;
            [DataMember] public string Mode,Host;
        }
        internal static T Read<T>(string path)
        {
            using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(path))))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
        internal static bool HostAllows(string mode,string host,string actual)=>mode=="BroadcastContinuity"&&host=="AIR-02"&&actual.Equals(host,StringComparison.OrdinalIgnoreCase);
        internal static bool Enabled
        {
            get{try{var p=Read<Policy>(Path.Combine(Home,"broadcast-continuity-qc.json"));return p.Version==1&&HostAllows(p.Mode,p.Host,Environment.MachineName);}catch{return false;}}
        }
        // Preserve the existing half-second shortfall guard. Only a small fully
        // decoded overrun can become a warning; incomplete outputs remain blocked.
        internal static bool DurationAllowed(double expected,double actual,bool continuity)
        {
            if(expected<=0||actual<=0||double.IsNaN(expected)||double.IsNaN(actual)||double.IsInfinity(expected)||double.IsInfinity(actual))return false;
            var delta=actual-expected;
            return delta>=-0.5&&delta<=(continuity?Math.Min(2,Math.Max(0.5,expected*0.001)):0.5);
        }
        [DataContract] internal sealed class RetryPlan
        {
            [DataMember] public string Generation;
            [DataMember] public RetryItem[] Jobs;
        }
        [DataContract] internal sealed class RetryItem
        {
            [DataMember] public string Id,InputHash,Reason;
        }
        internal static bool SoftFailure(string error)
        {
            if(string.IsNullOrEmpty(error))return false;
            var e=error.ToLowerInvariant();
            if(new[]{"decode failed","decoding error","invalid data","verification failed","hash mismatch","integrity","input changed","no video","no audio","corrupt","truncated"}.Any(e.Contains))return false;
            return new[]{"unknown source range","unknown/unsupported sdr source color tags","interlaced sources in this profile must have 25","durations are different"}.Any(e.Contains);
        }
        [DataContract] private sealed class AirEvent
        {
            [DataMember] public string AssetKey,Begin,ParseStatus;
            [DataMember] public long DurationTicks;
        }
        [DataContract] private sealed class Asset
        {
            [DataMember] public string Key,LogicalName;
        }
        private static readonly object AirSync=new object();
        private static DateTime _airRead;
        private static Dictionary<string,AirEvent[]> _air=new Dictionary<string,AirEvent[]>();
        internal static DateTime? Due(string name,DateTime now)
        {
            if(!(name??"").StartsWith("vplay_",StringComparison.Ordinal))return now;
            lock(AirSync){
                if((now-_airRead).TotalSeconds>=5){
                    try{
                        var events=Read<AirEvent[]>(Path.Combine(Home,"events.json"));
                        var assets=Read<Asset[]>(Path.Combine(Home,"catalog.json"));
                        var byKey=events.Where(e=>e.ParseStatus=="Pending"&&!string.IsNullOrEmpty(e.AssetKey)).GroupBy(e=>e.AssetKey).ToDictionary(g=>g.Key,g=>g.ToArray());
                        _air=assets.Where(a=>byKey.ContainsKey(a.Key)).ToDictionary(a=>a.LogicalName,a=>byKey[a.Key]);
                        _airRead=now;
                    }catch{return null;} // no authoritative schedule => no new VPlay encoding
                }
                if(!_air.TryGetValue(name,out var uses))return null;
                return uses.Select(e=>new{Begin=DateTime.SpecifyKind(DateTime.ParseExact(e.Begin,"yyyy-MM-dd'T'HH:mm:ss.fff",System.Globalization.CultureInfo.InvariantCulture),DateTimeKind.Unspecified),e.DurationTicks})
                    .Select(e=>new{Begin=TimeZoneInfo.ConvertTimeToUtc(e.Begin,TimeZoneInfo.FindSystemTimeZoneById("FLE Standard Time")),e.DurationTicks})
                    .Where(e=>e.Begin.AddTicks(e.DurationTicks)>now&&e.Begin<=now.AddHours(72))
                    .OrderBy(e=>e.Begin).Select(e=>(DateTime?)e.Begin).FirstOrDefault();
            }
        }
    }
}
