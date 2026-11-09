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
    static void Check(JObject node, bool rejected, string label)
    {
        bool failed = false;
        try { Validate.Invoke(null, new object[] { node, true, 0 }); } catch (TargetInvocationException e) when (e.InnerException is IOException) { failed = true; }
        if (failed != rejected) throw new Exception("FAIL: " + label); checks++; Console.WriteLine("PASS " + label);
    }
    [STAThread] static int Main(string[] args)
    {
        try {
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
            Persistence(); CacheRules(); Console.WriteLine(checks + " checks passed"); return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
