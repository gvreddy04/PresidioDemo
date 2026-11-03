using Python.Runtime;

namespace PresidioDemo.Protection;

/// <summary>
/// Starts embedded CPython from a virtual environment (the project's src/.venv by default) and calls python/presidio_demo.py,
/// which the build copies next to the application.
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
    /// <param name="pythonEnvironment">The virtual environment folder, or null for the project's src/.venv.</param>
    /// <exception cref="FileNotFoundException">A required local runtime or configuration file is missing.</exception>
    public static void Start(string? pythonEnvironment = null)
    {
        string venvDir = pythonEnvironment ?? Path.Combine(FindProjectDir(), "src", ".venv");

        // pyvenv.cfg records the base Python install ("home") and its version.
        var cfg = File.ReadAllLines(Path.Combine(venvDir, "pyvenv.cfg"))
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());

        string home = cfg["home"];
        string[] version = (cfg.GetValueOrDefault("version") ?? cfg["version_info"]).Split('.'); // uv writes version_info
        string pythonVersion = $"{version[0]}.{version[1]}";
        string moduleDir = Path.Combine(AppContext.BaseDirectory, "python");

        if (OperatingSystem.IsWindows())
        {
            // A Windows venv's home is the install folder, which holds the DLL and Lib.
            string pythonDll = Path.Combine(home, $"python{version[0]}{version[1]}.dll");
            if (!File.Exists(pythonDll))
            {
                throw new FileNotFoundException(
                            $"{pythonDll} not found. Was Python 3.12 uninstalled? Recreate src/.venv (README: Run).");
            }

            Runtime.PythonDLL = pythonDll;
            PythonEngine.PythonHome = home;
            PythonEngine.PythonPath = string.Join(Path.PathSeparator,
                Path.Combine(home, "Lib"),
                Path.Combine(home, "DLLs"),
                Path.Combine(venvDir, "Lib", "site-packages"),
                moduleDir);
        }
        else
        {
            // A Linux venv's home is the bin folder; the shared library is found on the system library path.
            string prefix = Path.GetDirectoryName(home) ?? home;
            Runtime.PythonDLL = $"libpython{pythonVersion}.so.1.0";
            PythonEngine.PythonHome = prefix;
            PythonEngine.PythonPath = string.Join(Path.PathSeparator,
                Path.Combine(prefix, "lib", "python" + pythonVersion),
                Path.Combine(prefix, "lib", "python" + pythonVersion, "lib-dynload"),
                Path.Combine(venvDir, "lib", "python" + pythonVersion, "site-packages"),
                moduleDir);
        }

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
}
