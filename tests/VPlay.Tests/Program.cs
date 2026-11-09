using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;

static class Program
{
    static int checks;
    static readonly MethodInfo Validate = typeof(TAS.Client.VPlayMigration.Plugin).GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic);
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
            Console.WriteLine(checks + " checks passed"); return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
