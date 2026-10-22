string root = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(root, "PresidioDemo.csproj")))
{
    root = Directory.GetParent(root)?.FullName ?? throw new Exception("Project root not found.");
}
string output = args.Length == 1 ? Path.GetFullPath(args[0]) :
    Path.Combine(root, ".test-runs", "round-trip-" + Guid.NewGuid().ToString("N"));
await NoAnonymizationRoundTripChecks.RunAsync(root, output);
