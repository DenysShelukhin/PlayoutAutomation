// EWTN 2026-10-30. Engine-owned admission and revision coordination; native ingest/copy/delete.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using TAS.Common;
using TAS.Common.Interfaces;
using TAS.Server.Media;
using TAS.Server.MediaOperation;

namespace TAS.Server
{
    [DataContract] internal sealed class IncomingJob
    {
        [DataMember] public Guid Id,Target;
        [DataMember] public string Name,Input,InputIdentity,InputHash,State="Queued",Error;
        [DataMember] public string Candidate,CandidateHash,NewFile,NewIdentity,OldFile,OldIdentity,OldHash;
        [DataMember] public string OldArchive,OldArchiveIdentity,OldArchiveHash;
        [DataMember] public long Created,Retry;
        [DataMember] public int Attempts;
        [DataMember] public bool InputDeleted,OldDeleted,OldArchiveDeleted;
        [DataMember] public IncomingSnapshot Previous;
    }
    [DataContract] internal sealed class IncomingSnapshot
    {
        [DataMember] public string File,Folder;
        [DataMember] public ulong Size;
        [DataMember] public long Duration,DurationPlay,Tc,TcPlay,Updated;
        [DataMember] public TVideoFormat Video;
        [DataMember] public TAudioChannelMapping Audio;
        [DataMember] public TMediaStatus Status;
        internal static IncomingSnapshot Take(ServerMedia m)=>new IncomingSnapshot{File=m.FileName,Folder=m.Folder,Size=m.FileSize,Duration=m.Duration.Ticks,DurationPlay=m.DurationPlay.Ticks,Tc=m.TcStart.Ticks,TcPlay=m.TcPlay.Ticks,Updated=m.LastUpdated.Ticks,Video=m.VideoFormat,Audio=m.AudioChannelMapping,Status=m.MediaStatus};
        internal void Restore(ServerMedia m){m.FileName=File;m.Folder=Folder;m.FileSize=Size;m.Duration=TimeSpan.FromTicks(Duration);m.DurationPlay=TimeSpan.FromTicks(DurationPlay);m.TcStart=TimeSpan.FromTicks(Tc);m.TcPlay=TimeSpan.FromTicks(TcPlay);m.LastUpdated=new DateTime(Updated,DateTimeKind.Utc);m.VideoFormat=Video;m.AudioChannelMapping=Audio;m.MediaStatus=Status;}
    }

    internal sealed class IncomingCoordinator : IDisposable
    {
        private readonly Engine _engine;
        private readonly MediaManager _manager;
        private readonly ArchiveAutomation _archive;
        private readonly List<IncomingJob> _jobs=new List<IncomingJob>();
        private readonly Dictionary<string,Observation> _observations=new Dictionary<string,Observation>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Guid> _encoding=new HashSet<Guid>(),_deleting=new HashSet<Guid>();
        private readonly object _sync=new object();
        private Timer _timer;private FileSystemWatcher _watcher;private int _running;private bool _disposed,_startupEncoderGate=true;
        private static readonly NLog.Logger Log=NLog.LogManager.GetCurrentClassLogger();
        private sealed class Observation {internal long Size;internal DateTime Stamp,Quiet;internal string Error;}
        internal IncomingCoordinator(Engine e,MediaManager manager,ArchiveAutomation archive)
        {
            _engine=e;_manager=manager;_archive=archive;
            if(!e.IncomingEnabled){manager.SetIncomingStatus("Incoming OFF");return;}
            try{
                if(e.IncomingQuietSeconds<5)throw new IOException("Incoming quiet time must be at least five seconds.");
                if(!string.IsNullOrWhiteSpace(e.IncomingJournal))using(var s=new MemoryStream(Encoding.UTF8.GetBytes(e.IncomingJournal)))_jobs.AddRange((List<IncomingJob>)new DataContractJsonSerializer(typeof(List<IncomingJob>)).ReadObject(s));
                ValidateRoots();
                _watcher=new FileSystemWatcher(e.IncomingFolder){NotifyFilter=NotifyFilters.FileName|NotifyFilters.Size|NotifyFilters.LastWrite,IncludeSubdirectories=false};
                _watcher.Created+=Changed;_watcher.Changed+=Changed;_watcher.Renamed+=Changed;_watcher.Error+=(_,a)=>Log.Warn(a.GetException(),"Incoming watcher; periodic scan remains active");_watcher.EnableRaisingEvents=true;
                _timer=new Timer(_=>Tick(),null,1000,1000);
            }catch(Exception ex){manager.SetIncomingStatus("Incoming FAILED/DISABLED: "+ex.Message);Log.Error(ex,"Incoming initialization");}
        }
        private void Changed(object s,FileSystemEventArgs e) { /* events are hints; the bounded periodic reconciliation is authoritative */ }
        private IngestDirectory Profile => _manager.IngestDirectories.OfType<IngestDirectory>().Single(d=>d.DirectoryName==_engine.IncomingIngestProfileName);
        private ServerDirectory Local => (ServerDirectory)_manager.MediaDirectoryPRI;
        private void ValidateRoots()
        {
            var p=Profile;p.ValidateBroadcastReadyV1();
            if(!p.BroadcastReadyV1 || p.DeleteSource || p.IsRecursive || p.Kind!=TIngestDirectoryKind.WatchFolder)throw new IOException("Managed profile must be flat WatchFolder / BR v1 / DeleteSource=false.");
            var roots=new[]{_engine.IncomingFolder,p.Folder,_engine.IncomingWorkFolder,Local.Folder};
            foreach(var r in roots){if(!Directory.Exists(r))throw new IOException("Configured Incoming root unavailable: "+r);IncomingFiles.Within(r,Path.Combine(r,"probe"));}
            foreach(var a in roots)foreach(var b in roots)if(a!=b && (Path.GetFullPath(a).TrimEnd('\\')+"\\").StartsWith(Path.GetFullPath(b).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))throw new IOException("Incoming/managed/work/media roots must be disjoint.");
            if(!string.Equals(Path.GetPathRoot(p.Folder),Path.GetPathRoot(_engine.IncomingFolder),StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetPathRoot(Local.Folder),Path.GetPathRoot(_engine.IncomingWorkFolder),StringComparison.OrdinalIgnoreCase))throw new IOException("Claim and publication must stay on their local volume.");
            if(_manager.IngestDirectories.OfType<IngestDirectory>().Any(d=>d!=p && string.Equals(d.Folder.TrimEnd('\\'),p.Folder.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)))throw new IOException("Managed folder has more than one profile owner.");
        }
        private void Persist()
        {
            // Keep bounded successful history, but never discard unfinished cleanup/revisions.
            while(_jobs.Count(j=>j.State=="Done")>200)_jobs.Remove(_jobs.First(j=>j.State=="Done"));
            using(var s=new MemoryStream()){new DataContractJsonSerializer(typeof(List<IncomingJob>)).WriteObject(s,_jobs);_engine.IncomingJournal=Encoding.UTF8.GetString(s.ToArray());}
            DatabaseProvider.Database.UpdateEngine(_engine);
        }
        private void Tick()
        {
            if(Interlocked.Exchange(ref _running,1)!=0)return;
            try{lock(_sync){if(_disposed || !Local.IsInitialized || !Profile.IsInitialized)return;
                if(_startupEncoderGate){if(System.Diagnostics.Process.GetProcessesByName("ffmpeg").Length>0){_manager.SetIncomingStatus("Incoming waiting: encoder from previous/external session still active");return;}_startupEncoderGate=false;}
                Scan();foreach(var j in _jobs.Where(j=>j.State!="Done"&&j.State!="Failed").OrderBy(j=>j.Created).ToArray()){
                    if(j.Retry>DateTime.UtcNow.Ticks)continue;
                    try{Advance(j);}catch(Exception ex){Fail(j,ex);}
                }
                _manager.SetIncomingStatus("Incoming ON; quiet="+_engine.IncomingQuietSeconds+"s; "+string.Join(" / ",_jobs.GroupBy(j=>j.State).Select(g=>g.Key+"="+g.Count()))+"\n"+
                    string.Join("\n",_observations.Where(o=>o.Value.Error!=null).Select(o=>Path.GetFileName(o.Key)+": "+o.Value.Error).Concat(_jobs.Where(j=>j.State!="Done").Select(j=>j.Name+" ["+j.State+"] "+j.Error)).Take(30)));
            }}catch(Exception ex){_manager.SetIncomingStatus("Incoming ERROR; inputs retained: "+ex.Message);Log.Error(ex,"Incoming pass");}
            finally{Interlocked.Exchange(ref _running,0);}
        }
        private void Scan()
        {
            var paths=Directory.EnumerateFiles(_engine.IncomingFolder).Where(p=>new[]{".mov",".mp4",".mpg",".mpeg",".mxf",".mkv",".avi",".ts",".m2ts",".wmv"}.Contains(Path.GetExtension(p).ToLowerInvariant())).Take(1000).ToArray();
            foreach(var stale in _observations.Keys.Where(p=>!paths.Contains(p,StringComparer.OrdinalIgnoreCase)).ToArray())_observations.Remove(stale);
            foreach(var path in paths){
                var f=new FileInfo(path);if(!_observations.TryGetValue(path,out var o)){_observations[path]=new Observation{Size=f.Length,Stamp=f.LastWriteTimeUtc,Quiet=DateTime.UtcNow};continue;}
                if(f.Length!=o.Size || f.LastWriteTimeUtc!=o.Stamp){o.Size=f.Length;o.Stamp=f.LastWriteTimeUtc;o.Quiet=DateTime.UtcNow;o.Error=null;continue;}
                if(f.Length==0 || (DateTime.UtcNow-o.Quiet).TotalSeconds<_engine.IncomingQuietSeconds)continue;
                if(o.Error!=null)continue; // retry only after a changed delivery; avoid repeatedly hashing a rejected stable file
                try{
                    using(var check=IncomingFiles.Open(_engine.IncomingFolder,path)){} // do not start ffprobe on a live writer
                    var probe=BroadcastProbe.Read(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ffprobe.exe"),path);
                    if(!probe.Streams.Any(s=>s.Type=="video") || double.Parse(probe.Format.Duration,System.Globalization.CultureInfo.InvariantCulture)<=0)throw new IOException("No readable video/duration; suspected incomplete delivery");
                    using(var handle=IncomingFiles.Open(_engine.IncomingFolder,path)){
                        f.Refresh();if(f.Length!=o.Size || f.LastWriteTimeUtc!=o.Stamp){o.Quiet=DateTime.UtcNow;continue;}
                        var j=new IncomingJob{Id=Guid.NewGuid(),Name=Path.GetFileNameWithoutExtension(path).Normalize(NormalizationForm.FormC),Created=DateTime.UtcNow.Ticks,State="Claiming",InputIdentity=IncomingFiles.Identity(handle),InputHash=IncomingFiles.Hash(handle)};
                        j.Input=j.Id.ToString("N")+Path.GetExtension(path).ToLowerInvariant();j.Candidate=j.Id.ToString("N")+".mov";
                        _jobs.Add(j);Persist();IncomingFiles.Move(handle,IncomingFiles.Within(Profile.Folder,Path.Combine(Profile.Folder,j.Input)));
                        IncomingFiles.Seal(Profile.Folder,Path.Combine(Profile.Folder,j.Input));j.State="Queued";Persist();
                    }
                    _observations.Remove(path);
                }catch(Win32Exception ex) when(ex.NativeErrorCode==32){o.Quiet=DateTime.UtcNow;}
                catch(Exception ex){o.Error=ex.Message;Log.Warn(ex,"Incoming delivery retained: {0}",Path.GetFileName(path));}
            }
        }
        private void Advance(IncomingJob j)
        {
            if(j.State=="Claiming"){
                var p=Path.Combine(Profile.Folder,j.Input);if(File.Exists(p)){using(var f=IncomingFiles.Open(Profile.Folder,p))if(IncomingFiles.Identity(f)!=j.InputIdentity || IncomingFiles.Hash(f)!=j.InputHash)throw new IOException("Claim recovery identity mismatch");IncomingFiles.Seal(Profile.Folder,p);j.State="Queued";Persist();}
                else{j.State="Failed";j.Error="Claim not completed; resend or review Incoming. No source was deleted.";Persist();}return;
            }
            if(j.State=="Encoding" && !_encoding.Contains(j.Id)){j.State="Queued";Persist();}
            if(j.State=="Queued"){StartIngest(j);return;}
            if(j.State=="Ready" || j.State=="Publishing"){Publish(j);return;}
            if(j.State=="Committed"){CleanupAndArchive(j);return;}
        }
        private void StartIngest(IncomingJob j)
        {
            if(_encoding.Count!=0 || Server.FileManager.Current.GetOperationQueue().OfType<IngestOperation>().Any())return;
            var path=IncomingFiles.Within(Profile.Folder,Path.Combine(Profile.Folder,j.Input));
            using(var f=IncomingFiles.Open(Profile.Folder,path))if(IncomingFiles.Identity(f)!=j.InputIdentity || IncomingFiles.Hash(f)!=j.InputHash)throw new IOException("Managed input changed; retained for review");
            var source=Profile.FindMediaFirst(m=>string.Equals(((MediaBase)m).FullPath,path,StringComparison.OrdinalIgnoreCase)) as IngestMedia;
            if(source==null){source=new IngestMedia{FileName=j.Input,Folder="",MediaName=j.Name,MediaGuid=j.Id,MediaType=TMediaType.Movie};Profile.AddMedia(source);}else Profile.UpdateMediaGuid(source,j.Id);
            source.Verify(true);if(!source.IsVerified || source.MediaStatus!=TMediaStatus.Available)throw new IOException("Input verification failed");
            var work=new IncomingWorkDirectory{Folder=_engine.IncomingWorkFolder};
            var op=(IngestOperation)Server.FileManager.Current.CreateFileOperation(TFileOperationKind.Ingest);
            op.Source=source;op.DestDirectory=work;op.ManagedIncoming=true;op.TryCount=1;op.StartTC=source.TcStart;op.Trim=false;
            op.DestProperties=new PersistentMediaProxy{MediaGuid=j.Id,MediaName=j.Name,FileName=j.Candidate,MediaType=TMediaType.Movie,VideoFormat=TVideoFormat.HD1080i5000,Duration=source.Duration,TcStart=source.TcStart};
            op.AspectConversion=TAspectConversion.NoConversion;op.SourceColorSpace=IngestColorSpace.Auto;
            j.State="Encoding";j.Error=null;Persist();_encoding.Add(j.Id);
            op.Finished+=(_,a)=>{lock(_sync){_encoding.Remove(j.Id);if(_disposed)return;
                try{if(op.OperationStatus==FileOperationStatus.Finished){j.CandidateHash=AtomicMediaCopy.Hash(op.Dest.FullPath);j.State="Ready";j.Error=null;}
                else{j.State="Failed";j.Error="Native Ingest failed; managed input retained. "+string.Join("; ",op.OperationOutput.AsEnumerable().Reverse().Take(2).Reverse());}}
                catch(Exception ex){j.State="Failed";j.Error="Completed job verification failed; input retained: "+ex.Message;}SafePersist();}};
            Server.FileManager.Current.Queue(op);
        }
        private static string Key(string s)=>(s??"").Normalize(NormalizationForm.FormC).ToUpperInvariant();
        internal static bool MatchesName(string name,string mediaName,string fileName,Guid guid)
        {
            if(Key(name)==Key(mediaName))return true;
            var physical=Path.GetFileNameWithoutExtension(fileName??"");
            if(physical.StartsWith(guid.ToString("N")+"_",StringComparison.OrdinalIgnoreCase))physical=physical.Substring(33);
            return Key(name)==Key(physical);
        }
        private ServerMedia Resolve(IncomingJob j)
        {
            var known=_jobs.Where(x=>x!=j && x.Target!=Guid.Empty && x.State=="Done" && Key(x.Name)==Key(j.Name)).OrderByDescending(x=>x.Created).FirstOrDefault();
            var matches=Local.GetAllFiles().OfType<ServerMedia>().Where(m=>MatchesName(j.Name,m.MediaName,m.FileName,m.MediaGuid) || (known!=null && m.MediaGuid==known.Target)).ToArray();
            if(matches.Length>1)throw new IOException("Ambiguous logical name; existing materials retained");
            if(matches.Length==1)return matches[0];
            var archived=((ArchiveDirectory)_manager.ArchiveDirectory).FindByLogicalName(j.Name);
            if(archived.Length>1)throw new IOException("Ambiguous archive logical name");
            var target=new ServerMedia{MediaGuid=j.Target!=Guid.Empty?j.Target:archived.Length==1?archived[0].MediaGuid:j.Id,MediaName=j.Name,LastUpdated=DateTime.UtcNow,Folder="",FileName="incoming_"+j.Id.ToString("N")+".mov",MediaType=TMediaType.Movie,MediaStatus=TMediaStatus.Required};Local.AddMedia(target);return target;
        }
        private bool Busy(Guid id)
        {
            var channels=((CasparServerChannel)_engine.PlayoutChannelPRI).Owner.ChannelsSer;
            return channels.Any(c=>!c.IsServerConnected || c.UsesMedia(id)) ||
                Server.FileManager.Current.GetOperationQueue().Any(o=>References(o,id));
        }
        private static bool References(object operation,Guid id)
        {
            if(operation is IngestOperation ingest && ingest.Dest?.MediaGuid==id)return true;
            foreach(var p in operation.GetType().GetProperties()){
                if(p.GetIndexParameters().Length!=0)continue;
                if(typeof(TAS.Common.Interfaces.Media.IMedia).IsAssignableFrom(p.PropertyType) && p.GetValue(operation) is TAS.Common.Interfaces.Media.IMedia m && m.MediaGuid==id)return true;
                if(typeof(IEnumerable<TAS.Common.Interfaces.Media.IMedia>).IsAssignableFrom(p.PropertyType) && p.GetValue(operation) is IEnumerable<TAS.Common.Interfaces.Media.IMedia> list && list.Any(item=>item.MediaGuid==id))return true;
            }return false;
        }
        private bool CasparStillUses(params string[] paths)
        {
            // Covers producers left in CasparCG across a TVPlay restart, including preload.
            // It is read-only AMCP; no consumer/output settings are changed.
            var owner=((CasparServerChannel)_engine.PlayoutChannelPRI).Owner;
            var endpoint=owner.ServerAddress.Split(':');var host=endpoint[0];int port=endpoint.Length>1?int.Parse(endpoint[1]):5250;
            foreach(var ch in owner.ChannelsSer){
                using(var client=new System.Net.Sockets.TcpClient()){
                    var connection=client.BeginConnect(host,port,null,null);if(!connection.AsyncWaitHandle.WaitOne(1000))return true;client.EndConnect(connection);
                    var stream=client.GetStream();stream.ReadTimeout=1000;var request=Encoding.UTF8.GetBytes("INFO "+ch.Id+"\r\n");stream.Write(request,0,request.Length);
                    var data=new StringBuilder();var buffer=new byte[16384];while(data.ToString().IndexOf("</channel>",StringComparison.Ordinal)<0){int n=stream.Read(buffer,0,buffer.Length);if(n==0)return true;data.Append(Encoding.UTF8.GetString(buffer,0,n));if(data.Length>1048576)return true;}
                    var response=data.ToString();int start=response.IndexOf("<?xml",StringComparison.Ordinal);if(start<0)start=response.IndexOf("<channel>",StringComparison.Ordinal);
                    var xml=new System.Xml.XmlDocument();xml.LoadXml(response.Substring(start).Trim());
                    foreach(System.Xml.XmlNode file in xml.SelectNodes("//filename"))if(paths.Where(p=>!string.IsNullOrEmpty(p)).Any(p=>string.Equals(file.InnerText,p,StringComparison.OrdinalIgnoreCase)))return true;
                }
            }
            return false;
        }
        private IEnumerable<IEvent> ScheduledUsers(Guid id)
        {
            var seen=new HashSet<IEvent>();var stack=new Stack<IEvent>(_engine.GetRootEvents());
            while(stack.Count>0){var e=stack.Pop();if(e==null||!seen.Add(e)||e.IsDeleted)continue;foreach(var child in e.GetSubEvents())stack.Push(child);var next=e.GetNext();if(next!=null)stack.Push(next);if(e.MediaGuid==id&&e.PlayState==TPlayState.Scheduled)yield return e;}
        }
        private string Token(string root,string path,out string hash)
        {
            using(var f=IncomingFiles.Inspect(root,path)){hash=IncomingFiles.Hash(f);return IncomingFiles.Identity(f);}
        }
        private void Publish(IncomingJob j)
        {
            if(_jobs.Any(x=>x!=j && Key(x.Name)==Key(j.Name) && x.Created<j.Created && x.State!="Done" && x.State!="Failed"))return;
            var candidatePath=Path.Combine(_engine.IncomingWorkFolder,j.Candidate);
            var target=j.Target==Guid.Empty?Resolve(j):Local.FindMediaByMediaGuid(j.Target) as ServerMedia;
            if(target==null && string.IsNullOrEmpty(j.OldFile) && (j.Previous==null || j.Previous.Status==TMediaStatus.Required))target=Resolve(j);
            if(target==null)throw new IOException("Target GUID missing; refusing to create another material");
            if(j.Target==Guid.Empty){j.Target=target.MediaGuid;Persist();}
            lock(IncomingMediaGate.Sync){if(Busy(j.Target) || _archive.HasPending(j.Target)){j.Error="Waiting: Program/preload/Preview or active media operation";return;}}
            if(CasparStillUses(target.FullPath)){j.Error="Waiting: CasparCG still holds the existing Program/preload producer";return;}
            // Hash old immutable files outside the load barrier. No exclusive locks on playout reads.
            if(j.State!="Publishing"){
                j.Previous=IncomingSnapshot.Take(target);
                if(target.FileExists()){IncomingFiles.Within(Local.Folder,target.FullPath);j.OldFile=target.FullPath;j.OldIdentity=Token(Local.Folder,j.OldFile,out j.OldHash);}
                var old=((ArchiveDirectory)_manager.ArchiveDirectory).Find(j.Target) as ArchiveMedia;
                if(old?.FileExists()==true){if(!Path.GetFullPath(old.FullPath).StartsWith(Path.GetFullPath(_manager.ArchiveDirectory.Folder).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))throw new IOException("Archive record escaped configured root; replacement rejected");j.OldArchive=old.FullPath;j.OldArchiveIdentity=Token(Path.GetDirectoryName(old.FullPath),old.FullPath,out j.OldArchiveHash);}
                j.NewFile="incoming_"+j.Target.ToString("N")+"_"+j.Id.ToString("N")+".mov";
                j.State="Publishing";Persist();
            }
            var newPath=IncomingFiles.Within(Local.Folder,Path.Combine(Local.Folder,j.NewFile));
            if(File.Exists(candidatePath) && AtomicMediaCopy.Hash(candidatePath)!=j.CandidateHash)throw new IOException("Prepared revision hash mismatch");
            lock(IncomingMediaGate.Sync){
                if(Busy(j.Target) || _archive.HasPending(j.Target)){j.Error="Waiting for material to become idle";return;}
                var staged=new IncomingWorkMedia{Directory=new IncomingWorkDirectory{Folder=File.Exists(candidatePath)?_engine.IncomingWorkFolder:Local.Folder},FileName=File.Exists(candidatePath)?j.Candidate:j.NewFile,MediaType=TMediaType.Movie,MediaStatus=TMediaStatus.Copied};
                staged.Verify(true);if(staged.MediaStatus!=TMediaStatus.Available)throw new IOException("Prepared revision not readable");
                if(ScheduledUsers(j.Target).Any(e=>e.ScheduledTc<staged.TcStart || e.ScheduledTc+e.Duration>staged.TcStart+staged.Duration))
                    throw new IOException("New revision is shorter than an existing scheduled cut; previous material retained for review");
                if(!File.Exists(newPath) && !File.Exists(candidatePath))throw new IOException("Prepared revision disappeared");
                bool confirmed=false;
                try {
                target.IsVerified=false;target.LastUpdated=DateTime.UtcNow;target.MediaStatus=TMediaStatus.Copying;target.FileName=j.NewFile;target.Folder="";target.Duration=staged.Duration;target.DurationPlay=staged.Duration;target.TcStart=staged.TcStart;target.TcPlay=staged.TcStart;target.VideoFormat=staged.VideoFormat;target.AudioChannelMapping=TAudioChannelMapping.Stereo;
                IncomingPersistence.Save(target,Local); // durable GUID/path before watcher sees final filename
                if(!File.Exists(newPath)){using(var f=IncomingFiles.Open(_engine.IncomingWorkFolder,candidatePath)){j.NewIdentity=IncomingFiles.Identity(f);IncomingFiles.Move(f,newPath);}}
                target.MediaStatus=TMediaStatus.Copied;target.Verify(true);if(target.MediaStatus!=TMediaStatus.Available)throw new IOException("Published revision failed native verification");
                target.TcPlay=target.TcStart;target.DurationPlay=target.Duration;IncomingPersistence.Save(target,Local);confirmed=true;
                using(var f=IncomingFiles.Inspect(Local.Folder,newPath)){if(j.NewIdentity!=null && IncomingFiles.Identity(f)!=j.NewIdentity)throw new IOException("Published identity changed");j.NewIdentity=IncomingFiles.Identity(f);}
                _archive.RequestCommittedRevision(target,j.CandidateHash);
                j.State="Committed";j.Error=null;Persist();_engine.NotifyMediaVerified(new MediaEventArgs(target));
                }catch{
                    j.State="Publishing";
                    // A confirmed SQL publication is monotonic. Retry archive/journal acknowledgement;
                    // never roll it back after archive revision intent may already be durable.
                    if(!confirmed){
                        // Park our immutable version before restoring the previous pointer, so the
                        // directory watcher cannot register an orphan under a different GUID.
                        if(File.Exists(newPath) && !File.Exists(candidatePath)){
                            using(var f=IncomingFiles.Open(Local.Folder,newPath)){
                                if(IncomingFiles.Hash(f)!=j.CandidateHash || (j.NewIdentity!=null && IncomingFiles.Identity(f)!=j.NewIdentity))throw new IOException("Unconfirmed publication changed; all copies retained for review");
                                IncomingFiles.Move(f,candidatePath);
                            }
                        }
                        j.Previous?.Restore(target);
                        try{IncomingPersistence.Save(target,Local);}catch(Exception e){Log.Error(e,"Previous revision pointer could not be persisted; raw/old files retained");}
                    }
                    throw;
                }
            }
        }
        private void DeleteOwned(IncomingJob j,string root,string path,string identity,string hash,Action completed)
        {
            if(_deleting.Contains(j.Id))return;
            var op=new IncomingDeleteOperation{Root=root,PathName=path,FileIdentity=identity,ContentHash=hash,TryCount=1};
            _deleting.Add(j.Id);op.Finished+=(_,a)=>{lock(_sync){_deleting.Remove(j.Id);if(_disposed)return;if(op.OperationStatus==FileOperationStatus.Finished){completed();j.Error=null;j.Retry=0;}else{j.Error="CleanupPending: "+string.Join("; ",op.OperationOutput.AsEnumerable().Reverse().Take(2).Reverse());j.Retry=DateTime.UtcNow.AddSeconds(30).Ticks;}SafePersist();}};
            Server.FileManager.Current.Queue(op);
        }
        private void CleanupAndArchive(IncomingJob j)
        {
            var media=Local.FindMediaByMediaGuid(j.Target) as ServerMedia;
            if(media==null || media.MediaStatus!=TMediaStatus.Available || !media.FileExists() || media.FileName!=j.NewFile)throw new IOException("Committed revision not available; input retained");
            if(!j.InputDeleted){if(!_engine.IncomingCleanupEnabled)return;DeleteOwned(j,Profile.Folder,Path.Combine(Profile.Folder,j.Input),j.InputIdentity,j.InputHash,()=>j.InputDeleted=true);return;}
            var archive=((ArchiveDirectory)_manager.ArchiveDirectory).Find(j.Target) as ArchiveMedia;
            if(_archive.HasPending(j.Target) || archive==null){j.Error="Waiting for native Auto Archive revision confirmation";return;}
            if(!archive.FileExists() || AtomicMediaCopy.Hash(archive.FullPath)!=j.CandidateHash)throw new IOException("Archive revision not SHA-confirmed");
            if(CasparStillUses(j.OldFile,j.OldArchive)){j.Error="Waiting to retire previous revision: CasparCG still references it";return;}
            if(!j.OldDeleted && !string.IsNullOrEmpty(j.OldFile) && !string.Equals(j.OldFile,media.FullPath,StringComparison.OrdinalIgnoreCase)){DeleteOwned(j,Path.GetDirectoryName(j.OldFile),j.OldFile,j.OldIdentity,j.OldHash,()=>j.OldDeleted=true);return;}
            if(!j.OldArchiveDeleted && !string.IsNullOrEmpty(j.OldArchive) && !string.Equals(j.OldArchive,archive.FullPath,StringComparison.OrdinalIgnoreCase)){DeleteOwned(j,Path.GetDirectoryName(j.OldArchive),j.OldArchive,j.OldArchiveIdentity,j.OldArchiveHash,()=>j.OldArchiveDeleted=true);return;}
            j.State="Done";j.Error=null;Persist();
        }
        private void Fail(IncomingJob j,Exception ex)
        {
            j.Error=ex.Message;j.Retry=DateTime.UtcNow.AddSeconds(Math.Min(600,30*Math.Pow(2,Math.Min(4,j.Attempts++)))).Ticks;
            Log.Error(ex,"Incoming {0} {1}; owned inputs/previous revision retained",j.Id,j.State);Persist();
        }
        private void SafePersist(){try{Persist();}catch(Exception e){Log.Error(e,"Incoming state not persisted; inputs retained until confirmed commit");_manager.SetIncomingStatus("Incoming journal save failed: "+e.Message);}}
        public void Dispose(){lock(_sync){_disposed=true;_timer?.Dispose();_watcher?.Dispose();}}
    }
}
