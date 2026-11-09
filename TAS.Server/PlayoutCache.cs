// EWTN 2026-11-09. Registered D: library -> verified C: playout mirror.
// Native Ingest/Archive/Restore remain authoritative. No DB deletion or second encoder.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using TAS.Common;
using TAS.Common.Interfaces;
using TAS.Common.Interfaces.Media;
using TAS.Server.Media;
using TAS.Server.MediaOperation;

namespace TAS.Server
{
    internal sealed class CacheSettings { public bool Enabled; public long MaxBytes=200000000000; public int HorizonHours=72; }
    internal sealed class CacheEntry
    {
        public Guid Guid; public string File,Identity,Hash,Source,Archive; public long Bytes,SourceStamp,ArchiveStamp;
    }
    internal sealed class PlayoutCache : IDisposable
    {
        private static PlayoutCache Active;
        private readonly Engine _engine; private readonly MediaManager _manager; private readonly ArchiveAutomation _automation;
        private readonly object _sync=new object(); private readonly Dictionary<Guid,CacheEntry> _entries=new Dictionary<Guid,CacheEntry>();
        private readonly Dictionary<Guid,DateTime> _demand=new Dictionary<Guid,DateTime>();
        private readonly CancellationTokenSource _cancel=new CancellationTokenSource();
        private readonly string _home=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),@"EWTN\Playout\Migration");
        private readonly string _root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),@"EWTN\Playout\Cache");
        private CacheSettings _settings; private Timer _timer; private int _running; private bool _disposed; private string _error;
        internal PlayoutCache(Engine engine,MediaManager manager,ArchiveAutomation automation)
        {
            _engine=engine;_manager=manager;_automation=automation;
            var config=Path.Combine(_home,"cache-settings.json");if(!File.Exists(config))return;
            _settings=JsonConvert.DeserializeObject<CacheSettings>(File.ReadAllText(config));if(_settings?.Enabled!=true)return;
            if(!PlayoutCacheRules.Fits(0,0,0,_settings.MaxBytes)||_settings.HorizonHours<1||_settings.HorizonHours>72)throw new IOException("Invalid cache policy");
            if(!Directory.Exists(_root)||new DriveInfo(Path.GetPathRoot(_root)).DriveFormat!="NTFS")throw new IOException("Private NTFS C: cache root required");
            IncomingFiles.Within(_root,Path.Combine(_root,"ownership-check"));
            var index=Path.Combine(_home,"cache-index.json");if(File.Exists(index))foreach(var entry in JsonConvert.DeserializeObject<List<CacheEntry>>(File.ReadAllText(index))) {
                IncomingFiles.Within(_root,Path.Combine(_root,entry.File));if(_entries.ContainsKey(entry.Guid))throw new IOException("Duplicate cache GUID");_entries.Add(entry.Guid,entry);
            }
            if(Active!=null)throw new IOException("Only one cache owner supported");Active=this;
            _timer=new Timer(_=>Tick(),null,1000,3000);
        }
        private bool Applies(IMedia m)=>m is ServerMedia && m.MediaType==TMediaType.Movie;
        internal static bool CanServe(MediaBase m,bool request=true) {var cache=Active;return cache==null||!cache.Applies(m)||cache.PathFor(m,request)!=null;}
        internal static string PlaybackName(MediaBase m,string fallback) {var cache=Active;if(cache==null||!cache.Applies(m))return fallback;var path=cache.PathFor(m);if(path==null)throw new IOException("Not Ready: playout cache pending");return "\""+path+"\"";}
        private string PathFor(MediaBase media,bool request=true)
        {
            lock(_sync){
                if(Used()<=_settings.MaxBytes&&_entries.TryGetValue(media.MediaGuid,out var e)&&Current(e,media)&&Available(e))return Path.Combine(_root,e.File);
                if(request)_demand[media.MediaGuid]=_engine.CurrentTime.AddMinutes(10);return null;
            }
        }
        private bool Current(CacheEntry e,MediaBase m)=>e.Source==m.FullPath && e.Bytes==(long)m.FileSize && File.Exists(m.FullPath)&&File.GetLastWriteTimeUtc(m.FullPath).Ticks==e.SourceStamp;
        private bool Available(CacheEntry e)
        {
            var path=Path.Combine(_root,e.File);if(!File.Exists(path)||new FileInfo(path).Length!=e.Bytes||File.GetLastWriteTimeUtc(path).Ticks!=e.SourceStamp)return false;
            try{using(var f=IncomingFiles.Inspect(_root,path))return IncomingFiles.Identity(f)==e.Identity;}catch(IOException){return false;}catch(System.ComponentModel.Win32Exception){return false;}
        }
        private long Used(){if(Directory.EnumerateDirectories(_root).Any())throw new IOException("Unmanaged cache subdirectory; admission blocked");return Directory.EnumerateFiles(_root).Sum(p=>new FileInfo(p).Length);}
        private Dictionary<Guid,DateTime> Requests()
        {
            var result=new Dictionary<Guid,DateTime>();var seen=new HashSet<IEvent>();var stack=new Stack<IEvent>(_engine.GetRootEvents());var now=_engine.CurrentTime;
            while(stack.Count>0){var e=stack.Pop();if(e==null||e.IsDeleted||!seen.Add(e))continue;foreach(var child in e.GetSubEvents())stack.Push(child);if(e.GetNext()!=null)stack.Push(e.GetNext());
                // Disabled imported inventory may request preparation; it never requests Play.
                if(e.EventType==TEventType.Movie&&e.MediaGuid!=Guid.Empty&&(e.IsEnabled||(e.IdAux??"").StartsWith("VP26E:"))&&PlayoutCacheRules.InWindow(now,e.ScheduledTime,_settings.HorizonHours))
                    if(!result.ContainsKey(e.MediaGuid)||e.ScheduledTime<result[e.MediaGuid])result[e.MediaGuid]=e.ScheduledTime;
            }
            lock(_sync)foreach(var pair in _demand.Where(p=>p.Value>=now).ToArray())if(!result.ContainsKey(pair.Key))result[pair.Key]=now;
            return result;
        }
        private bool Protected(Guid id,CacheEntry entry)
        {
            var owner=((CasparServerChannel)_engine.PlayoutChannelPRI).Owner;
            if(owner.ChannelsSer.Any(c=>!c.IsServerConnected||c.UsesMedia(id)))return true;
            if(Server.FileManager.Current.GetOperationQueue().Any(op=>References(op,id)))return true;
            // Read-only physical producer check covers producers left after a restart.
            var endpoint=owner.ServerAddress.Split(':');foreach(var channel in owner.ChannelsSer)using(var client=new System.Net.Sockets.TcpClient()){
                if(!client.ConnectAsync(endpoint[0],endpoint.Length>1?int.Parse(endpoint[1]):5250).Wait(1000))return true;
                var stream=client.GetStream();stream.ReadTimeout=1000;var request=System.Text.Encoding.UTF8.GetBytes("INFO "+channel.Id+"\r\n");stream.Write(request,0,request.Length);
                var data=new System.Text.StringBuilder();var buffer=new byte[16384];while(!data.ToString().Contains("</channel>")){var n=stream.Read(buffer,0,buffer.Length);if(n==0||data.Length>1048576)return true;data.Append(System.Text.Encoding.UTF8.GetString(buffer,0,n));}
                var text=data.ToString();int begin=text.IndexOf("<?xml",StringComparison.Ordinal);if(begin<0)return true;var xml=new System.Xml.XmlDocument();xml.LoadXml(text.Substring(begin).Trim());
                foreach(System.Xml.XmlNode file in xml.SelectNodes("//filename"))if(string.Equals(file.InnerText,Path.Combine(_root,entry.File),StringComparison.OrdinalIgnoreCase))return true;
            }return false;
        }
        private static bool References(object operation,Guid id)
        {
            foreach(var property in operation.GetType().GetProperties())if(property.GetIndexParameters().Length==0){
                if(typeof(IMedia).IsAssignableFrom(property.PropertyType)&&property.GetValue(operation) is IMedia m&&m.MediaGuid==id)return true;
                if(typeof(IEnumerable<IMedia>).IsAssignableFrom(property.PropertyType)&&property.GetValue(operation) is IEnumerable<IMedia> list&&list.Any(item=>item.MediaGuid==id))return true;
            }return false;
        }
        private bool ArchiveConfirmed(CacheEntry entry,MediaBase current)
        {
            if(_automation.HasPending(entry.Guid))return false;var archived=(_manager.ArchiveDirectory as ArchiveDirectory)?.Find(entry.Guid) as ArchiveMedia;
            if(archived==null||archived.MediaStatus!=TMediaStatus.Available||!archived.FileExists())return false;
            if(archived.FullPath==entry.Archive&&File.GetLastWriteTimeUtc(archived.FullPath).Ticks==entry.ArchiveStamp&&(long)archived.FileSize==entry.Bytes)return true;
            return false; // Changed archive proof: retain the cache until explicitly reconciled.
        }
        private void Evict(Dictionary<Guid,DateTime> needed)
        {
            var local=(ServerDirectory)_manager.MediaDirectoryPRI;
            foreach(var entry in Snapshot()){
                var media=local.FindMediaByMediaGuid(entry.Guid) as MediaBase;
                if(needed.ContainsKey(entry.Guid)&&media!=null&&Current(entry,media))continue;
                // Verify content outside the producer gate; large hashes must not stall transitions.
                if(File.Exists(Path.Combine(_root,entry.File))&&(!Available(entry)||AtomicMediaCopy.Hash(Path.Combine(_root,entry.File))!=entry.Hash))continue;
                // Same order as native playback: rundown -> producer/media gate.
                // Requests/GetSubEvents takes RundownSync and must never invert that order.
                lock(_engine.RundownSync) lock(IncomingMediaGate.Sync){
                    if(needed.ContainsKey(entry.Guid)&&media!=null&&Current(entry,media))continue;
                    if(Requests().ContainsKey(entry.Guid)&&media!=null&&Current(entry,media))continue;
                    if(Protected(entry.Guid,entry)||!ArchiveConfirmed(entry,media))continue;
                    if(File.Exists(Path.Combine(_root,entry.File))&&!Available(entry))continue;
                    IncomingFiles.Delete(_root,Path.Combine(_root,entry.File),entry.Identity,null);
                    lock(_sync)_entries.Remove(entry.Guid);Persist();
                }
            }
        }
        private CacheEntry[] Snapshot(){lock(_sync)return _entries.Values.ToArray();}
        private void Persist()=>Save("cache-index.json",Snapshot());
        private void Save(string name,object value)
        {
            var path=Path.Combine(_home,name);var temp=path+".tmp";File.WriteAllText(temp,JsonConvert.SerializeObject(value,Formatting.Indented));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
        }
        private void Tick()
        {
            if(Interlocked.Exchange(ref _running,1)!=0)return;
            try{
                if(_disposed)return;var local=(ServerDirectory)_manager.MediaDirectoryPRI;var needed=Requests();Evict(needed);_error=null;
                foreach(var pair in needed.OrderBy(p=>p.Value)){
                    if(_disposed)return;var media=local.FindMediaByMediaGuid(pair.Key) as ServerMedia;
                    if(media==null||media.MediaStatus!=TMediaStatus.Available||!media.FileExists()){
                        var archived=(_manager.ArchiveDirectory as ArchiveDirectory)?.Find(pair.Key);
                        if(archived!=null&&archived.MediaStatus==TMediaStatus.Available&&!Server.FileManager.Current.GetOperationQueue().Any(op=>References(op,pair.Key)))((ArchiveDirectory)_manager.ArchiveDirectory).ArchiveRestore(archived,local);
                        continue;
                    }
                    lock(_sync)if(_entries.ContainsKey(pair.Key))continue; // retained/protected revision cannot be replaced yet
                    if(_automation.HasPending(pair.Key))continue;
                    var archive=(_manager.ArchiveDirectory as ArchiveDirectory)?.Find(pair.Key) as ArchiveMedia;if(archive==null||archive.MediaStatus!=TMediaStatus.Available||!archive.FileExists())continue;
                    var source=media.FullPath;var stamp=File.GetLastWriteTimeUtc(source).Ticks;var size=checked((long)media.FileSize);
                    var archivePath=archive.FullPath;var archiveStamp=File.GetLastWriteTimeUtc(archivePath).Ticks;
                    using(var sourceLease=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    if(sourceLease.Length!=size)throw new IOException("Source byte count changed; cache reservation refused");
                    var hash=AtomicMediaCopy.Hash(source);
                    if(hash!=AtomicMediaCopy.Hash(archivePath)){_error="Not Ready: archive revision differs";continue;}
                    var filename=pair.Key.ToString("N")+"_"+hash+".mov";var destination=Path.Combine(_root,filename);
                    long incoming=File.Exists(destination)?0:size;
                    if(!PlayoutCacheRules.Fits(Used(),0,incoming,_settings.MaxBytes)||new DriveInfo(Path.GetPathRoot(_root)).AvailableFreeSpace<incoming+64L*1024*1024*1024){_error="Not Ready: cache capacity/reserve exhausted; protected/upcoming files retained";break;}
                    AtomicMediaCopy.Copy(source,destination,pair.Key,_cancel.Token,null).GetAwaiter().GetResult();
                    lock(IncomingMediaGate.Sync){
                        if(media.FullPath!=source||File.GetLastWriteTimeUtc(source).Ticks!=stamp||media.FileSize!=(ulong)size||archive.FullPath!=archivePath||File.GetLastWriteTimeUtc(archivePath).Ticks!=archiveStamp)throw new IOException("Source/archive revision changed during cache copy");
                        string identity;using(var f=IncomingFiles.Inspect(_root,destination)){identity=IncomingFiles.Identity(f);if(IncomingFiles.Hash(f)!=hash)throw new IOException("Cache content verification failed");}
                        lock(_sync){if(_entries.ContainsKey(pair.Key))throw new IOException("Previous protected revision still cached");_entries[pair.Key]=new CacheEntry{Guid=pair.Key,File=filename,Identity=identity,Hash=hash,Source=source,SourceStamp=stamp,Archive=archivePath,ArchiveStamp=archiveStamp,Bytes=size};}Persist();
                    }
                    break; // one copy per pass, nearest air time first
                    }
                }
                var used=Used();if(used>_settings.MaxBytes)throw new IOException("External/unowned cache files exceed approved quota; playback admission blocked");
                Save("cache-status.json",new{Utc=DateTime.UtcNow,Root=_root,Bytes=used,MaximumBytes=_settings.MaxBytes,HorizonHours=_settings.HorizonHours,Needed=needed.Count,Ready=Snapshot().Count(e=>needed.ContainsKey(e.Guid)&&Available(e)),Error=_error,Entries=Snapshot()});
                _manager.SetCacheStatus("Playout cache: "+used+" / "+_settings.MaxBytes+" bytes; "+_settings.HorizonHours+"h",_error);
            }catch(OperationCanceledException){}catch(Exception e){_error=e.Message;_manager.SetCacheStatus("Playout cache needs attention",_error);}
            finally{Interlocked.Exchange(ref _running,0);}
        }
        public void Dispose(){_disposed=true;_cancel.Cancel();_timer?.Dispose();if(Active==this)Active=null;}
    }
}
