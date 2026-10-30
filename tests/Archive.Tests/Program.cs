using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using TAS.Common;
using TAS.Server;
using TAS.Server.MediaOperation;

internal static class Program
{
    static int checks;
    static void Check(bool pass, string name) { if (!pass) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static async Task Tests(string root)
    {
        var now = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
        Check(ArchivePreparationRules.InWindow(now, now.AddHours(72), 72, false), "72h inclusive boundary");
        Check(!ArchivePreparationRules.InWindow(now, now.AddHours(72).AddTicks(1), 72, false), "outside horizon not restored");
        Check(ArchivePreparationRules.InWindow(now, now.AddDays(7), 72, true), "entire rundown includes seven days");
        Check(!ArchivePreparationRules.InWindow(now, now.AddDays(7), 72, false), "seven days excluded in horizon mode");
        Check(ArchivePreparationRules.InWindow(now, now.AddMinutes(-5), 24, false), "late imminent item retained for warning");
        Check(!ArchivePreparationRules.InWindow(now, now.AddHours(-1), 24, false), "old schedule not hydrated by default");
        Check(!ArchivePreparationRules.HasSpace(100, 0, new ulong[0]), "no free space refused");
        Check(!ArchivePreparationRules.HasSpace(100, 64 * 1024 * 1024, new ulong[0]), "reserve protected");
        Check(ArchivePreparationRules.HasSpace(100, 100 + 64 * 1024 * 1024, new ulong[0]), "exact reservation boundary");
        Check(!ArchivePreparationRules.HasSpace(100, 200 + 64 * 1024 * 1024, new ulong[] { 101 }), "active job reservation deducted");
        Check(!ArchivePreparationRules.HasSpace(ulong.MaxValue, long.MaxValue, new ulong[0]), "overflow fails closed");
        Check((ArchivePreparationRules.NextRetry(now, 1) - now).TotalSeconds == 30, "first retry delayed30s");
        Check((ArchivePreparationRules.NextRetry(now, 100) - now).TotalSeconds == 600, "backoff bounded10minutes");
        var intent = new ArchiveIntent { Guid = Guid.NewGuid(), Name = "lab", Phase = "Copying", Folder = "202610", File = "reserved.mov", Attempts = 2, RetryUtcTicks = now.AddMinutes(1).Ticks, IngestConfirmed = true };
        var serializer = new DataContractJsonSerializer(typeof(List<ArchiveIntent>));
        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, new List<ArchiveIntent> { intent }); stream.Position = 0;
            var recovered = ((List<ArchiveIntent>)serializer.ReadObject(stream)).Single();
            Check(recovered.Guid == intent.Guid && recovered.File == intent.File && recovered.RetryUtcTicks == intent.RetryUtcTicks && recovered.IngestConfirmed, "durable destination/retry and native completion acknowledgement roundtrip");
        }
        using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("[{\"Guid\":\"" + Guid.NewGuid() + "\",\"Phase\":\"Failed\"}]")))
        {
            var unconfirmed = ((List<ArchiveIntent>)serializer.ReadObject(stream)).Single();
            Check(!unconfirmed.IngestConfirmed, "interrupted intent without native success acknowledgement recovers unconfirmed even in Failed phase");
        }
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.bin"); var dest = Path.Combine(root, "result.bin");
        var bytes = new byte[4 * 1024 * 1024]; new Random(773).NextBytes(bytes); File.WriteAllBytes(source, bytes);
        var guid = Guid.NewGuid(); var before = AtomicMediaCopy.Hash(source);
        await AtomicMediaCopy.Copy(source, dest, guid, CancellationToken.None, null);
        Check(before == AtomicMediaCopy.Hash(dest), "published copy SHA256 matches source");
        var stamp = File.GetLastWriteTimeUtc(dest);
        await AtomicMediaCopy.Copy(source, dest, guid, CancellationToken.None, null);
        Check(File.GetLastWriteTimeUtc(dest) == stamp && Directory.GetFiles(root).Length == 2, "repeat completed transfer is no-op without duplicate file");
        var cancelled = Path.Combine(root, "cancelled.bin"); var cancel = new CancellationTokenSource(); bool interrupted = false;
        try { await AtomicMediaCopy.Copy(source, cancelled, guid, cancel.Token, _ => cancel.Cancel()); } catch (OperationCanceledException) { interrupted = true; }
        Check(interrupted && !File.Exists(cancelled) && Directory.GetFiles(root, "*.partial").Length == 0, "cancelled transfer never publishes partial and cleans owned staging");
        File.WriteAllText(dest, "preserve me"); var existing = AtomicMediaCopy.Hash(dest); bool conflict = false;
        try { await AtomicMediaCopy.Copy(source, dest, guid, CancellationToken.None, null); } catch (IOException) { conflict = true; }
        Check(conflict && AtomicMediaCopy.Hash(dest) == existing, "conflicting published file never overwritten");
        Check(AtomicMediaCopy.Hash(source) == before, "all failures preserve source bytes");
        var gate = new TaskCompletionSource<bool>(); var count = 0; var max = 0;
        var queue = new FileOperationQueue { MaxConcurrent = 2 };
        var jobs = Enumerable.Range(0, 3).Select(_ => new TestOperation(async () => { var n = Interlocked.Increment(ref count); max = Math.Max(max, n); await gate.Task; Interlocked.Decrement(ref count); return true; })).ToArray();
        foreach (var job in jobs) queue.Enqueue(job);
        for (int i = 0; i < 100 && count < 2; i++) await Task.Delay(10);
        Check(count == 2 && jobs.Count(j => j.OperationStatus == FileOperationStatus.InProgress) == 2, "native queue runs exactly two concurrent jobs");
        gate.SetResult(true);
        for (int i = 0; i < 100 && jobs.Any(j => j.OperationStatus != FileOperationStatus.Finished); i++) await Task.Delay(10);
        Check(max == 2 && jobs.All(j => j.OperationStatus == FileOperationStatus.Finished), "native queue completes without exceeding concurrency");
        var failure = new TestOperation(() => Task.FromResult(false)) { TryCount = 1 };
        queue.Enqueue(failure);
        for (int i = 0; i < 100 && failure.OperationStatus != FileOperationStatus.Failed; i++) await Task.Delay(10);
        Check(failure.OperationStatus == FileOperationStatus.Failed && failure.TryCount == 0, "automatic native operation fails once; coordinator owns delayed retry");
        var incoming=Path.Combine(root,"incoming");var managed=Path.Combine(root,"managed");Directory.CreateDirectory(incoming);Directory.CreateDirectory(managed);
        var delivery=Path.Combine(incoming,"delivery.bin");File.WriteAllBytes(delivery,bytes);
        bool locked=false;
        using(var writer=new FileStream(delivery,FileMode.Open,FileAccess.Write,FileShare.ReadWrite)){
            try{using(var f=IncomingFiles.Open(incoming,delivery)){} }catch(System.ComponentModel.Win32Exception e){locked=e.NativeErrorCode==32;}
        }
        Check(locked && File.Exists(delivery),"open writer blocks exclusive admission without moving input");
        var moved=Path.Combine(managed,"owned.bin");string identity,hash;
        using(var lease=IncomingFiles.Open(incoming,delivery)){identity=IncomingFiles.Identity(lease);hash=IncomingFiles.Hash(lease);IncomingFiles.Move(lease,moved);}
        Check(!File.Exists(delivery)&&File.Exists(moved)&&AtomicMediaCopy.Hash(moved)==hash,"by-handle same-volume move preserves bytes without copy");
        using(var lease=IncomingFiles.Open(managed,moved))Check(IncomingFiles.Identity(lease)==identity,"atomic move preserves NTFS file identity");
        bool escaped=false;try{IncomingFiles.Delete(incoming,moved,identity,hash);}catch(IOException){escaped=true;}
        Check(escaped&&File.Exists(moved),"cleanup rejects files outside configured managed root");
        bool wrong=false;try{IncomingFiles.Delete(managed,moved,"other-identity",hash);}catch(IOException){wrong=true;}
        Check(wrong&&File.Exists(moved),"cleanup rejects substituted file identity");
        wrong=false;try{IncomingFiles.Delete(managed,moved,identity,"other-hash");}catch(IOException){wrong=true;}
        Check(wrong&&File.Exists(moved),"cleanup rejects changed content");
        IncomingFiles.Seal(managed,moved);
        IncomingFiles.Delete(managed,moved,identity,hash);
        Check(!File.Exists(moved),"sealed owned file removed through guarded by-handle disposition");
        IncomingFiles.Delete(managed,moved,identity,hash);
        Check(!File.Exists(moved),"repeated cleanup of confirmed missing file is idempotent");
        Check(IncomingCoordinator.MatchesName("PROGRAM","Edited title","Program.mov",guid),"legacy physical filename retains replacement identity after title editing");
        Check(IncomingCoordinator.MatchesName("program","Edited title",guid.ToString("N")+"_Program.mov",guid),"native archive GUID prefix does not hide legacy logical filename");
        Check(!IncomingCoordinator.MatchesName("other","Program","Program.mov",guid),"different filename is not treated as replacement");
        var journal=new IncomingJob{Id=Guid.NewGuid(),Target=Guid.NewGuid(),State="Publishing",Name="lab",InputIdentity=identity,InputHash=hash,NewFile="revision.mov",Previous=new IncomingSnapshot{File="old.mov",Duration=123}};
        using(var s=new MemoryStream()){
            var js=new DataContractJsonSerializer(typeof(List<IncomingJob>));js.WriteObject(s,new List<IncomingJob>{journal});s.Position=0;var copy=((List<IncomingJob>)js.ReadObject(s)).Single();
            Check(copy.Target==journal.Target&&copy.InputHash==hash&&copy.Previous.File=="old.mov"&&copy.State=="Publishing","interrupted revision journal retains GUID, ownership and previous pointer");
        }
    }
    sealed class TestOperation : FileOperationBase
    {
        readonly Func<Task<bool>> run;
        internal TestOperation(Func<Task<bool>> action) { run = action; }
        protected override Task<bool> InternalExecute() => run();
        protected override void OnOperationStatusChanged() { }
    }
    static int Main(string[] args)
    {
        try { if (args.Length != 1) throw new ArgumentException("Disposable test directory required"); Tests(args[0]).GetAwaiter().GetResult(); Console.WriteLine("ALL " + checks + " CHECKS PASS"); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
