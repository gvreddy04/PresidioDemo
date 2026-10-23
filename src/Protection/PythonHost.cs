using Python.Runtime;

namespace PresidioDemo.Protection;

#region Classes

/// <summary>
/// Starts embedded CPython using the project's src/.venv and calls src/Protection/python/presidio_demo.py.
/// </summary>
public static class PythonHost
{

    #region Fields and Constants

    /// <summary>
    /// The loaded local Python module used for configured field protection.
    /// </summary>
    private static PyObject? _module;

    #endregion

    #region Methods

    /// <summary>
    /// Invokes a named function in the embedded local Python module while holding the Python GIL.
    /// </summary>
    /// <param name="function">The name of the existing local Python bridge function to invoke.</param>
    /// <param name="args">The string arguments passed to the embedded Python function.</param>
    /// <returns>The string returned by the configured local Python function.</returns>
    public static string Call(string function, params string[] args)
    {
        using (Py.GIL())
        {
            PyObject[] pyArgs = args.Select(a => new PyString(a)).ToArray<PyObject>();
            try
            {
                using (PyObject result = _module!.InvokeMethod(function, pyArgs))
                {
                    return result.As<string>();
                }
            }
            finally
            {
                foreach (var argument in pyArgs)
                {
                    argument.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Initializes embedded CPython and loads the existing local Presidio module and bundled NLP model.
    /// </summary>
    /// <exception cref="FileNotFoundException">A required local runtime or configuration file is missing.</exception>
    public static void Start()
    {
        string projectDir = ProjectDir = FindProjectDir();
        string venvDir = Path.Combine(projectDir, "src", ".venv");

        // pyvenv.cfg records the base Python install ("home") and its version.
        var cfg = File.ReadAllLines(Path.Combine(venvDir, "pyvenv.cfg"))
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());

        string pythonHome = cfg["home"];
        string[] version = (cfg.GetValueOrDefault("version") ?? cfg["version_info"]).Split('.'); // uv writes version_info

        string pythonDll = Path.Combine(pythonHome, $"python{version[0]}{version[1]}.dll");
        if (!File.Exists(pythonDll))
        {
            throw new FileNotFoundException(
                        $"{pythonDll} not found. Was Python 3.12 uninstalled? Recreate src/.venv (README: Run).");
        }

        Runtime.PythonDLL = pythonDll;
        PythonEngine.PythonHome = pythonHome;
        PythonEngine.PythonPath = string.Join(Path.PathSeparator,
            Path.Combine(pythonHome, "Lib"),
            Path.Combine(pythonHome, "DLLs"),
            Path.Combine(venvDir, "Lib", "site-packages"),
            Path.Combine(projectDir, "src", "Protection", "python"));

        PythonEngine.Initialize();
        PythonEngine.BeginAllowThreads();

        using (Py.GIL())
        {
            _module = Py.Import("presidio_demo");
        }
    }

    /// <summary>
    /// Shuts down the embedded Python runtime after active processing has drained.
    /// </summary>
    public static void Stop()
    {
        try { PythonEngine.Shutdown(); } catch { }
    }

    // Walk up from bin/Debug/net10.0 until the folder that contains src/.venv.
    /// <summary>
    /// Locates the project containing the existing embedded-Python environment.
    /// </summary>
    /// <returns>The project directory containing the existing src/.venv environment.</returns>
    /// <exception cref="DirectoryNotFoundException">The required local project or runtime directory cannot be found.</exception>
    private static string FindProjectDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", ".venv")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "No src/.venv folder found in the project folder. Create it first (README: Run).");
    }

    #endregion

    #region Properties, Indexers

    /// <summary>
    /// The folder that contains src/ and docs/.
    /// </summary>
    /// <value>The project directory containing the existing Python environment and feed workspace.</value>
    public static string ProjectDir { get; private set; } = "";

    #endregion
}

#endregion
