using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using TAS.Common.Interfaces;
using TAS.Common;
using System.Linq;

static class Program
{
    static int checks;
    private sealed class Stub : RealProxy, System.Runtime.Remoting.IRemotingTypeInfo
    {
        internal readonly Dictionary<string, object> Values = new Dictionary<string, object>();
        private readonly Type _contract;
        internal Stub(Type type) : base(type) { _contract = type; }
        public string TypeName { get; set; }
        public bool CanCastTo(Type type, object instance) => type.IsAssignableFrom(_contract) || type == typeof(IPersistent);
        public override IMessage Invoke(IMessage message)
        {
            var call = (IMethodCallMessage)message; var method = (MethodInfo)call.MethodBase; object result = null;
            if (method.Name.StartsWith("set_")) Values[method.Name.Substring(4)] = call.Args[0];
            else if (method.Name.StartsWith("get_")) { if (!Values.TryGetValue(method.Name.Substring(4), out result) && method.ReturnType.IsValueType) result = Activator.CreateInstance(method.ReturnType); }
            return new ReturnMessage(result, null, 0, call.LogicalCallContext, call);
        }
    }
    private static void Persistence()
    {
        // Isolated in-memory provider, never DatabaseSQLite.Open (which targets TVPlay.db).
        using (var connection = new SQLiteConnection("Data Source=:memory:;Version=3;")) {
            connection.Open(); var provider = new TAS.Database.SQLite.DatabaseSQLite();
            typeof(TAS.Database.SQLite.DatabaseBase).GetField("Connection", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(provider, connection);
            provider.CreateEmptyDatabase();
            var engine = new Stub(typeof(IEngine)); engine.Values["Id"] = 1UL;
            var stub = new Stub(typeof(IEventPersistent)); stub.Values["Engine"] = engine.GetTransparentProxy();
            stub.Values["IdAux"] = "VP26E:0123456789abcdef0123456789abcdef"; stub.Values["EventName"] = "persistence test";
            stub.Values["Duration"] = TimeSpan.FromSeconds(2); stub.Values["EventType"] = TEventType.Movie;
            stub.Values["ScheduledTime"] = DateTime.UtcNow; stub.Values["RouterPort"] = -1;
            var e = (IEventPersistent)stub.GetTransparentProxy();
            if (!provider.InsertEvent(e)) throw new Exception("Native provider INSERT failed");
            using (var command = new SQLiteCommand("select idAux from rundownevent", connection))
                if ((string)command.ExecuteScalar() != e.IdAux) throw new Exception("INSERT lost IdAux");
            checks++; Console.WriteLine("PASS native SQLite INSERT retains source identity");
            stub.Values["IdAux"] = "VP26E:fedcba9876543210fedcba9876543210";
            if (!provider.UpdateEvent(e)) throw new Exception("Native provider UPDATE failed");
            using (var command = new SQLiteCommand("select idAux from rundownevent", connection))
                if ((string)command.ExecuteScalar() != e.IdAux) throw new Exception("UPDATE lost IdAux");
            checks++; Console.WriteLine("PASS native SQLite UPDATE retains source identity");
            stub.Values["IdAux"] = null;
            if (!provider.UpdateEvent(e)) throw new Exception("Legacy null identity update failed");
            checks++; Console.WriteLine("PASS native SQLite legacy null identity remains supported");
        }
    }
    static readonly MethodInfo Validate = typeof(TAS.Client.VPlayMigration.Plugin).GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic);
    static void CacheRules()
    {
        Action<bool,string> check=(ok,label)=>{if(!ok)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);};
        long max=TAS.Server.PlayoutCacheRules.HardMaximum;
        check(TAS.Server.PlayoutCacheRules.Fits(max,0,0,max),"cache exact decimal 200GB boundary");
        check(!TAS.Server.PlayoutCacheRules.Fits(max,0,1,max),"cache one byte over budget refused");
        check(!TAS.Server.PlayoutCacheRules.Fits(0,0,max+1,max),"oversized full file refused");
        check(!TAS.Server.PlayoutCacheRules.Fits(max-20,15,6,max),"reservations counted before publication");
        check(TAS.Server.PlayoutCacheRules.Fits(max-20,15,5,max),"remaining reserved capacity accepted");
        check(!TAS.Server.PlayoutCacheRules.Fits(long.MaxValue,0,1,max),"cache arithmetic cannot overflow");
        check(!TAS.Server.PlayoutCacheRules.Fits(-1,0,0,max),"invalid negative accounting refused");
        var now=DateTime.UtcNow;
        check(TAS.Server.PlayoutCacheRules.InWindow(now,now.AddHours(72),72),"72h cache boundary included");
        check(!TAS.Server.PlayoutCacheRules.InWindow(now,now.AddHours(72).AddTicks(1),72),"outside 72h excluded");
        check(!TAS.Server.PlayoutCacheRules.InWindow(now,now.AddTicks(-1),72),"past events excluded from automatic cache demand");
        check(!TAS.Server.PlayoutCacheRules.InWindow(now,now.AddHours(1),73),"unapproved horizon rejected");
        check(!TAS.Server.PlayoutCacheRules.InWindow(now.AddDays(4),now.AddHours(1),72),"moving air clock expires prior-window demand");
    }
    static void BoundedOverrides()
    {
        var method=typeof(TAS.Client.VPlayMigration.Plugin).GetMethod("FileOverrideSourceAllowed",BindingFlags.Static|BindingFlags.NonPublic);
        var source=JObject.Parse("{Sequence:1,ItemId:'item',AssetKey:'asset',XmlSha256:'xml',Kind:'File',ItemType:'1',EventType:'2',DurationTicks:40000000,TimeCodeInTicks:0,StartTimecodeTicks:0,OutOffsetTicks:40000000,ClipDurationTicks:40000000,OriginalAttributes:{LogoIndex:'0'},Issues:['EventType requires semantics review']}");
        source["AudioXml"]="<AudioConfig Matrix_0='1' Matrix_1='1' Gain_0='1' Gain_1='1'/>";
        var approval=JObject.Parse("{Approved:true,Sequence:1,ItemId:'item',AssetKey:'asset',XmlSha256:'xml',DurationTicks:40000000,BeginUtcTicks:100}");
        Action<JObject,JObject,bool,string> check=(s,approved,expected,label)=>{if((bool)method.Invoke(null,new object[]{s,approved,100L})!=expected)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);};
        check(source,approval,true,"exact bounded file-only approval accepted");
        var n=(JObject)source.DeepClone();n["TimeCodeInTicks"]=400000;check(n,approval,false,"nonzero trim cannot bypass review");
        n=(JObject)source.DeepClone();n["Issues"]=new JArray("Unknown colour");check(n,approval,false,"technical QC warning cannot bypass review");
        n=(JObject)source.DeepClone();n["ClipDurationTicks"]=null;check(n,approval,false,"unknown source duration refused");
        n=(JObject)source.DeepClone();n["EventType"]="3";check(n,approval,false,"live or other special semantics refused");
        n=(JObject)source.DeepClone();n["AudioXml"]="<AudioConfig Matrix_0='0' Matrix_1='1' Gain_0='1' Gain_1='1'/>";check(n,approval,false,"nondefault audio routing refused");
        n=(JObject)source.DeepClone();n["OriginalAttributes"]["LogoIndex"]="1";check(n,approval,false,"unresolved logo action refused");
        var a=(JObject)approval.DeepClone();a["BeginUtcTicks"]=101;check(source,a,false,"approval cannot rebase original anchor");
        a=(JObject)approval.DeepClone();a["AssetKey"]="other";check(source,a,false,"approval cannot transfer to another file");
        a=(JObject)approval.DeepClone();a["Approved"]=false;check(source,a,false,"unapproved qualification cannot play");
    }
    static void AbsoluteBackground()
    {
        var loop=typeof(TAS.Server.CasparServerChannel).GetMethod("LoopAbsoluteDiagnostic",BindingFlags.Static|BindingFlags.NonPublic);
        var loopCases=new[]{
            new object[]{true,TEventType.Movie,"VP26T:fixture","ID_diagnostic",true,true},
            new object[]{false,TEventType.Movie,"VP26T:fixture","ID_diagnostic",true,false},
            new object[]{true,TEventType.Movie,"VP26E:source","ID_diagnostic",true,false},
            new object[]{true,TEventType.Movie,"VP26T:fixture","ordinary",true,false},
            new object[]{true,TEventType.Movie,"VP26T:fixture","ID_diagnostic",false,false},
            new object[]{true,TEventType.Live,"VP26T:fixture","ID_diagnostic",true,false},
            new object[]{true,TEventType.Movie,null,null,true,false}
        };
        foreach(var c in loopCases){if((bool)loop.Invoke(null,c.Take(5).ToArray())!=(bool)c[5])throw new Exception("Absolute diagnostic producer loop scope");checks++;Console.WriteLine("PASS absolute diagnostic producer loop scope");}
        var method=typeof(TAS.Server.CasparServerChannel).GetMethod("PreserveDiagnosticBackground",BindingFlags.Static|BindingFlags.NonPublic);
        for(int changed=-1;changed<5;changed++){
            var flags=new object[]{true,true,true,true,true};if(changed>=0)flags[changed]=false;
            bool preserve=(bool)method.Invoke(null,flags);
            if(preserve!=(changed<0))throw new Exception("Diagnostic background protection scope "+changed);
            checks++;Console.WriteLine("PASS absolute diagnostic protection scope "+changed);
        }
        var drift=typeof(TAS.Server.Engine).GetMethod("AbsoluteDriftNeedsCorrection",BindingFlags.Static|BindingFlags.NonPublic);
        var cases=new[]{new long[]{0,50,1933,1},new long[]{49,50,1933,0},new long[]{2147483647,50,1933,0},new long[]{100,50,1933,1},new long[]{0,-1,1933,0},new long[]{0,1933,1933,0},new long[]{0,1,0,0}};
        foreach(var c in cases){if((bool)drift.Invoke(null,new object[]{c[0],c[1],c[2]})!=(c[3]==1))throw new Exception("Physical drift bounds");checks++;Console.WriteLine("PASS physical foreground drift bounds "+c[0]+"/"+c[1]);}
    }
    static void Check(JObject node, bool rejected, string label)
    {
        bool failed = false;
        try { Validate.Invoke(null, new object[] { node, true, 0 }); } catch (TargetInvocationException e) when (e.InnerException is IOException) { failed = true; }
        if (failed != rejected) throw new Exception("FAIL: " + label); checks++; Console.WriteLine("PASS " + label);
    }
    static void ReconcileIndexes()
    {
        var type=typeof(TAS.Client.VPlayMigration.Plugin);
        var identity=(Func<string,string>)Delegate.CreateDelegate(typeof(Func<string,string>),type.GetMethod("NativeSourceIdentity",BindingFlags.Static|BindingFlags.NonPublic));
        string id="0123456789abcdef0123456789abcdef";
        foreach(var pair in new[]{new[]{"VP26E:"+id,id},new[]{"VP26T:prefix"+id,id},new[]{"VP26R:"+id,null},new[]{"VP26E:short",null},new string[]{null,null}}){
            if(identity(pair[0])!=pair[1])throw new Exception("Reconcile native identity scope");checks++;Console.WriteLine("PASS indexed reconciliation preserves source identity scope");
        }
        var boundary=type.GetMethod("CanReconcile",BindingFlags.Static|BindingFlags.NonPublic);var now=DateTime.UtcNow;
        foreach(var pair in new[]{new[]{0d,0d},new[]{1d,0d},new[]{2d,0d},new[]{2.001,1d},new[]{10d,1d}}){
            if((bool)boundary.Invoke(null,new object[]{now,(DateTime?)now.AddSeconds(pair[0])})!=(pair[1]==1))throw new Exception("Reconciliation boundary guard");checks++;Console.WriteLine("PASS noncritical reconciliation defers near original air boundary");
        }
        if(!(bool)boundary.Invoke(null,new object[]{now,null}))throw new Exception("No boundary reconciliation");checks++;
    }
    static void ReconcileBenchmark(string[] args)
    {
        var source=JArray.Parse(File.ReadAllText(args[1]));var catalog=JArray.Parse(File.ReadAllText(args[2]));
        var native=JArray.Parse(File.ReadAllText(args[3])).Select(x=>(string)x["IdAux"]).Where(x=>x!=null&&(x.StartsWith("VP26E:")||x.StartsWith("VP26T:"))).ToArray();
        var method=typeof(TAS.Client.VPlayMigration.Plugin).GetMethod("NativeSourceIdentity",BindingFlags.Static|BindingFlags.NonPublic);
        var identity=(Func<string,string>)Delegate.CreateDelegate(typeof(Func<string,string>),method);
        var clock=System.Diagnostics.Stopwatch.StartNew();var before=new List<string>();
        foreach(var asset in catalog){var key=(string)asset["Key"];foreach(var s in source.Where(s=>(string)s["AssetKey"]==key)){
            if(!Guid.TryParse((string)asset["MediaGuid"],out var guid)||guid==Guid.Empty)continue;
            var id=((string)s["ItemId"]).Replace("-","");foreach(var n in native.Where(n=>n.EndsWith(id,StringComparison.Ordinal)))before.Add(s["Sequence"]+":"+n);
        }}
        var legacy=clock.Elapsed.TotalMilliseconds;clock.Restart();var after=new List<string>();
        var byAsset=source.ToLookup(s=>(string)s["AssetKey"],StringComparer.Ordinal);var byId=native.ToLookup(identity,StringComparer.Ordinal);
        foreach(var asset in catalog)foreach(var s in byAsset[(string)asset["Key"]]){
            if(!Guid.TryParse((string)asset["MediaGuid"],out var guid)||guid==Guid.Empty)continue;
            foreach(var n in byId[((string)s["ItemId"]).Replace("-","")])after.Add(s["Sequence"]+":"+n);
        }
        var indexed=clock.Elapsed.TotalMilliseconds;if(before.Count==0||!before.SequenceEqual(after))throw new Exception("Actual reconciliation join mapping changed");
        Console.WriteLine("Actual read-only joins: sources="+source.Count+", assets="+catalog.Count+", identity pairs="+after.Count+", legacyMs="+legacy.ToString("F3")+", indexedMs="+indexed.ToString("F3"));
        Console.WriteLine("PASS identical ordered event/native identity pairs; no runtime or database mutation");
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            if(args.Length==4&&args[0]=="--reconcile-joins"){ReconcileBenchmark(args);return 0;}
            var historical = JObject.Parse(File.ReadAllText(args[0])); var test = JObject.Parse(File.ReadAllText(args[1]));
            Check(historical, false, "Full source schedule: disabled fixed-time tree accepted");
            Check(test, false, "Manual sequential test accepted");
            var n = (JObject)test.DeepClone(); n["StartType"] = 3; Check(n, true, "Auto-start root rejected");
            n = (JObject)historical.DeepClone(); n["SubEvents"][0]["SubEvents"][0]["IsEnabled"] = true; Check(n, true, "Enabled historical event rejected");
            n = (JObject)test.DeepClone(); n["SubEvents"][0]["Duration"] = "00:00:01.001"; Check(n, true, "Nonframe movie duration rejected");
            n = (JObject)test.DeepClone(); n["SubEvents"][1]["StartType"] = 1; Check(n, true, "Broken sequential chain rejected");
            n = (JObject)test.DeepClone(); n["SubEvents"][0]["EventType"] = 4; Check(n, true, "Unmapped live input rejected");
            n = (JObject)test.DeepClone(); n["SubEvents"][0]["IdAux"] = ""; Check(n, true, "Missing trace identity rejected");
            n = (JObject)test.DeepClone(); n["SubEvents"][0]["SubEvents"] = null; Check(n, true, "Missing explicit leaf array rejected");
            n = (JObject)test.DeepClone(); n["SubEvents"][0]["Duration"] = "-00:00:01"; Check(n, true, "Negative duration rejected");
            Persistence(); CacheRules(); BoundedOverrides(); AbsoluteBackground(); ReconcileIndexes(); Console.WriteLine(checks + " checks passed"); return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
