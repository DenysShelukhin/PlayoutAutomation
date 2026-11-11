// Physical foreground verification is asynchronous: AMCP I/O never holds the air clock.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Xml;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TAS.Common.Interfaces;

namespace TAS.Client.VPlayMigration
{
    public sealed partial class Plugin
    {
        private int _physicalBusy;
        private DateTime _physicalAt,_physicalCorrected;
        private void CheckPhysicalClock(IEvent ev,DateTime anchor,DateTime end)
        {
            var now=DateTime.UtcNow;
            if(now-_physicalAt<TimeSpan.FromSeconds(2)||Interlocked.CompareExchange(ref _physicalBusy,1,0)!=0)return;
            _physicalAt=now;
            var engine=Engine as TAS.Server.Engine;var channel=engine?.PlayoutChannelPRI as TAS.Server.CasparServerChannel;
            if(channel==null){Interlocked.Exchange(ref _physicalBusy,0);return;}
            var principal=Thread.CurrentPrincipal;
            Task.Run(()=>{
                var previousPrincipal=Thread.CurrentPrincipal;
                Thread.CurrentPrincipal=principal;
                try{
                    var endpoint=channel.PhysicalClockServerAddress.Split(':');
                    if(endpoint[0]!="127.0.0.1"&&endpoint[0]!="localhost")return;
                    int port=endpoint.Length>1?int.Parse(endpoint[1]):5250;
                    string expected;
                    var cache=JArray.Parse(File.ReadAllText(Path.Combine(Home,"cache-index.json"))).Cast<JObject>().SingleOrDefault(c=>(string)c["Guid"]==ev.MediaGuid.ToString());
                    if(cache==null)return;
                    expected=Path.Combine(Path.GetDirectoryName(Home),"Cache",(string)cache["File"]);
                    string response="";var requestAt=DateTime.UtcNow;
                    using(var client=new TcpClient()){
                        if(!client.ConnectAsync(endpoint[0],port).Wait(1500))return;
                        var stream=client.GetStream();stream.ReadTimeout=3000;
                        var command=Encoding.UTF8.GetBytes("INFO "+channel.Id+"\r\n");stream.Write(command,0,command.Length);
                        var buffer=new byte[16384];var data=new StringBuilder();
                        while(data.ToString().IndexOf("</channel>",StringComparison.Ordinal)<0){int n=stream.Read(buffer,0,buffer.Length);if(n==0)return;data.Append(Encoding.UTF8.GetString(buffer,0,n));if(data.Length>1048576)return;}
                        response=data.ToString();
                    }
                    var sampledAt=DateTime.UtcNow;var xml=new XmlDocument();int offset=response.IndexOf("<?xml",StringComparison.Ordinal);if(offset<0)return;
                    xml.LoadXml(response.Substring(offset).Trim());
                    var producer=xml.SelectSingleNode("/channel/stage/layers/layer[index="+(int)ev.Layer+"]/foreground/producer");
                    if(producer?.SelectSingleNode("type")?.InnerText=="transition-producer")producer=producer.SelectSingleNode("destination/producer");
                    var actualPath=producer?.SelectSingleNode("filename")?.InnerText;long actual;
                    if(actualPath==null||!long.TryParse(producer.SelectSingleNode("file-frame-number")?.InnerText,out actual))return;
                    bool matched=string.Equals(Path.GetFullPath(actualPath),Path.GetFullPath(expected),StringComparison.OrdinalIgnoreCase);
                    long? requested=null;bool repaired=false;
                    if(sampledAt<end&&sampledAt-requestAt<TimeSpan.FromSeconds(1)&&sampledAt-_physicalCorrected>TimeSpan.FromSeconds(5)){
                        if(matched)requested=engine.CorrectAbsolutePosition(ev,anchor,actual,sampledAt);
                        else if(sampledAt-anchor>TimeSpan.FromSeconds(3))repaired=engine.RepairAbsoluteForeground(ev,anchor);
                        if(requested.HasValue||repaired)_physicalCorrected=DateTime.UtcNow;
                    }
                    var report=new{Utc=sampledAt,EventId=ev.Id,MediaGuid=ev.MediaGuid,AnchorUtc=anchor,EndUtc=end,ActualFile=actualPath,ExpectedFile=expected,Matched=matched,
                        ActualFrame=actual,ExpectedOffsetSeconds=(sampledAt-anchor).TotalSeconds,QuerySeconds=(sampledAt-requestAt).TotalSeconds,RequestedFrame=requested,RepairedForeground=repaired,
                        CanPlay=engine.HaveRight(TAS.Common.EngineRight.Play),EnginePlayingId=engine.Playing?.Id,EngineUtc=engine.CurrentTime,MediaDurationTicks=ev.Media?.Duration.Ticks};
                    Save("absolute-physical-status.json",report);
                    if(requested.HasValue||repaired)File.AppendAllText(Path.Combine(Home,"absolute-physical-corrections.jsonl"),JsonConvert.SerializeObject(report)+Environment.NewLine);
                }catch(Exception ex){Save("absolute-physical-error.json",new{Utc=DateTime.UtcNow,Error=ex.Message});}
                finally{Thread.CurrentPrincipal=previousPrincipal;Interlocked.Exchange(ref _physicalBusy,0);}
            });
        }
    }
}
