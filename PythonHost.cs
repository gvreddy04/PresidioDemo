using Python.Runtime;

namespace PresidioDemo;

/// <summary>Starts embedded CPython using the project's .venv and calls python/presidio_demo.py.</summary>
public static class PythonHost
{
    private static PyObject? _module;

    /// <summary>The folder that contains .venv, python/ and docs/.</summary>
    public static string ProjectDir { get; private set; } = "";

    public static void Start()
    {
        string projectDir = ProjectDir = FindProjectDir();
        string venvDir = Path.Combine(projectDir, ".venv");

        // pyvenv.cfg records the base Python install ("home") and its version.
        var cfg = File.ReadAllLines(Path.Combine(venvDir, "pyvenv.cfg"))
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());

        string pythonHome = cfg["home"];
        string[] version = (cfg.GetValueOrDefault("version") ?? cfg["version_info"]).Split('.'); // uv writes version_info

        string pythonDll = Path.Combine(pythonHome, $"python{version[0]}{version[1]}.dll");
        if (!File.Exists(pythonDll))
            throw new FileNotFoundException(
                $"{pythonDll} not found. Was Python 3.12 uninstalled? Recreate .venv (README: Quick start).");

        Runtime.PythonDLL = pythonDll;
        PythonEngine.PythonHome = pythonHome;
        PythonEngine.PythonPath = string.Join(Path.PathSeparator,
            Path.Combine(pythonHome, "Lib"),
            Path.Combine(pythonHome, "DLLs"),
            Path.Combine(venvDir, "Lib", "site-packages"),
            Path.Combine(projectDir, "python"));

        PythonEngine.Initialize();
        PythonEngine.BeginAllowThreads();

        using (Py.GIL())
        {
            _module = Py.Import("presidio_demo");
        }
    }

    public static string Call(string function, params string[] args)
    {
        using (Py.GIL())
        {
            PyObject[] pyArgs = args.Select(a => new PyString(a)).ToArray<PyObject>();
            using PyObject result = _module!.InvokeMethod(function, pyArgs);
            return result.As<string>();
        }
    }

    public static void Stop()
    {
        try { PythonEngine.Shutdown(); } catch { }
    }

    // Walk up from bin/Debug/net10.0 until the folder that contains .venv.
    private static string FindProjectDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".venv")))
                return dir.FullName;

        throw new DirectoryNotFoundException(
            "No .venv folder found in the project folder. Create it first (README: Quick start).");
    }
}
