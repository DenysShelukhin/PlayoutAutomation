// GPL-2.0; see LICENSE. Experimental AIR-02 only; one lane is the fail-closed default.
using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace TAS.Server.MediaOperation
{
    internal static class NativeIngestPolicy
    {
        [DataContract] internal sealed class Settings
        {
            [DataMember] public int Version,MaxParallelJobs;
            [DataMember] public string Host;
            // A bounded native acceptance can restrict only the extra lane.
            // The primary lane always keeps the existing air-time priority.
            [DataMember] public string[] SecondLaneJobIds;
        }
        internal static int Limit(int version,string host,string actual,int requested,bool continuity)
            =>continuity&&version==1&&host=="AIR-02"&&actual.Equals(host,StringComparison.OrdinalIgnoreCase)&&requested>=1&&requested<=2?requested:1;
        internal static Settings Read()
        {
            try{
                var p=BroadcastContinuityQc.Read<Settings>(Path.Combine(BroadcastContinuityQc.Home,"native-ingest-lanes.json"));
                p.MaxParallelJobs=Limit(p.Version,p.Host,Environment.MachineName,p.MaxParallelJobs,BroadcastContinuityQc.Enabled);return p;
            }catch{return new Settings{MaxParallelJobs=1};}
        }
        internal static bool CanStart(Settings p,Guid id,int managed,int native,bool previousSameName)
            =>!previousSameName&&managed<p.MaxParallelJobs&&native<p.MaxParallelJobs&&
              (managed==0&&native==0||p.SecondLaneJobIds==null||p.SecondLaneJobIds.Length==0||p.SecondLaneJobIds.Contains(id.ToString(),StringComparer.OrdinalIgnoreCase));
        internal static bool Precedes(long created,Guid id,long otherCreated,Guid otherId)
            =>otherCreated<created||otherCreated==created&&otherId.CompareTo(id)<0;
        internal static bool WakeQueued(string state,string error,string reason)
            =>state=="Queued"&&!string.IsNullOrEmpty(error)&&
              reason=="UnknownStreamDuration"&&error=="Input verification failed";
        internal static bool ContainerDurationFallback(bool continuity,bool managed,bool videoPresent,double video,double audio,double file)
        {
            if(!continuity||!managed||!videoPresent||double.IsNaN(video)||double.IsNaN(audio)||double.IsNaN(file)||
                double.IsInfinity(video)||double.IsInfinity(audio)||double.IsInfinity(file))return false;
            // Unknown stream duration is not a measured A/V discrepancy. Use
            // agreeing positive audio/container metadata only for managed input;
            // the existing encoder -xerror and full output QC remain mandatory.
            return video<=0&&audio>0&&file>0&&Math.Abs(audio-file)<=0.5;
        }
    }
}
