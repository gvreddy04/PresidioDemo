using PresidioDemo.Protection;
using PresidioDemo.Spike;

#region Execution

// Test-only spike for decoded-text detection.
//   The parity baseline (tests/fixtures/parity-baseline.json) was frozen from the removed field policies and is
//   read-only; see ParityBaseline.
//   config-checks   Loads the five recognizer files and requires invalid files to stop startup (V-04).
//   bridge-json     Prints each feed's Presidio bridge configuration, one JSON document per line (Python tests).
//   detection-log on|off   Runs all samples through the decoded-text path and prints the value-free detection log
//                          when on (V-05). The worker reads the same switch from Presidio:DetectionLog.
//   spike [rounds]  Parity, PNRs, rebuilds, false positives, negative samples and
//                   throughput (V-01, V-02, V-11). Writes .test-runs/spike-*/summary.json (value-free) and report.json
//                   (values, Local only). Exit 0 when parity holds, 2 when it does not.
// Exit codes: 0 success, 1 failure.
string root = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(root, "PresidioDemo.slnx")))
{
    root = Directory.GetParent(root)?.FullName ?? throw new Exception("Project root not found.");
}
if (args is ["config-checks"])
{
    Console.WriteLine($"PASS {ConfigurationChecks.Run(root)} recognizer configuration checks");
    return 0;
}
if (args is ["bridge-json"])
{
    foreach (var set in RecognizerConfiguration.Load(Path.Combine(root, "src", "PresidioDemo.Core", "Configuration", "recognizers")).Values)
    {
        Console.WriteLine(set.ToBridgeJson());
    }
    return 0;
}
if (args is ["detection-log", "on" or "off"])
{
    PythonHost.Start();
    try
    {
        await DetectionRun.WriteLogAsync(root, args[1] == "on");
        return 0;
    }
    finally
    {
        PythonHost.Stop();
    }
}
if (args is ["spike"] or ["spike", _])
{
    int rounds = args.Length == 2 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 20;
    PythonHost.Start();
    try
    {
        string run = Path.Combine(root, ".test-runs", "spike-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        return await Spike.RunAsync(root, run, rounds) ? 0 : 2;
    }
    finally
    {
        PythonHost.Stop();
    }
}
Console.Error.WriteLine("Usage: DetectionSpike config-checks | bridge-json | detection-log on|off | spike [rounds]");
return 1;

#endregion
