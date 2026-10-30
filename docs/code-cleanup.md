# C# formatting and cleanup

Use the project-root [.editorconfig](../.editorconfig) as the shared formatting configuration. Visual Studio reads it automatically. No extension or additional NuGet package is required. The same file applies to the application and C# checks under `tests`.

EditorConfig is the best fit for this project: the built-in formatter covers whitespace, braces and imports. StyleCop adds broader analysis, but its default region and member-order rules differ from the conventions here and would need customization. It also cannot write meaningful member descriptions or safely decide initializer order.

## Run in Visual Studio

1. Open `PresidioDemo.slnx` and open a C# file.
2. Select **Analyze > Code Cleanup > Configure Code Cleanup**, or use the arrow beside the editor's broom icon.
3. Configure a profile with formatting, sorting/removing unnecessary usings, brace preferences, using-directive placement and namespace preferences, where those fixers are available in your Visual Studio version. Keep other transformations out of this profile unless you intend to apply them. The profile selection is a local Visual Studio setting; it is not stored in `.editorconfig`.
4. Press **Ctrl+K, Ctrl+E** to clean the current file. To process the project, right-click it in Solution Explorer and select **Analyze and Code Cleanup > Run Code Cleanup**. The solution currently includes the application project; open the individual check projects to clean their files too.

For whitespace formatting only, use **Ctrl+K, Ctrl+D**. For cleanup on save, enable **Run Code Cleanup profile on save** under **Tools > Options > Text Editor > Code Cleanup**.

## What is covered

| Convention | Support |
| --- | --- |
| Four spaces, CRLF, UTF-8, final newline, trailing whitespace | EditorConfig text settings and formatting |
| Opening braces on a new line; standard indentation and spacing | Formatting |
| Braces around one-statement control-flow bodies | `IDE0011`; include the brace fixer in cleanup |
| Prefer scoped `using (...) { ... }` blocks | `IDE0063` preference; the installed .NET 10 formatter leaves existing `using var` declarations unchanged, so converting those back to blocks requires review |
| System imports first; imports outside namespaces | Sort-usings fixer and `IDE0065` |
| File-scoped namespaces | `IDE0161` and its namespace fixer |
| Missing visible API documentation, malformed XML and mismatched parameter tags | Compiler warnings where XML documentation generation is enabled; the application already enables it |
| Meaningful XML descriptions for every member, including private helpers | Review; compiler checks do not cover this completely |
| Member regions and alphabetical/accessibility member order; omit the outer `Classes` region for a file with one top-level class | Review; built-in cleanup does not insert/remove regions or reorder members |
| Keep initialized fields and enum numeric/wire order stable | Review before any manual member movement |
| No new annotation attributes | Review; no cleanup setting inserts them |

Single-line expression members and compact auto-properties remain allowed. This configuration does not mandate converting between `var` and explicit types or between block and expression bodies. Choose only the cleanup fixers listed above to preserve that flexibility.

Style diagnostics appear in the editor. Build-time enforcement of IDE style rules is a separate opt-in (`EnforceCodeStyleInBuild`); this change does not enable it. Existing compiler documentation warnings remain active for the application. Generated C# is marked as generated for analysis. Non-C# payloads and historical files are outside the cleanup commands below.

## Run from a terminal

From the project root, after restoring/building the projects, format application whitespace:

```powershell
dotnet format whitespace PresidioDemo.slnx --no-restore --include src
```

Apply only the selected built-in style rules (including suggestion-level rules):

```powershell
dotnet format style PresidioDemo.slnx --no-restore --include src --severity info --diagnostics IDE0005 IDE0011 IDE0063 IDE0065 IDE0161
```

Use the same commands with an individual check project's `.csproj` and `--include tests`, or process all five check projects:

```powershell
$checkProjects = @(
    'tests/ACI/AciChecks.csproj'
    'tests/ETKT/EtktChecks.csproj'
    'tests/JsonFeeds/JsonFeedChecks.csproj'
    'tests/FeedPipelineChecks/FeedPipelineChecks.csproj'
    'tests/FeedRoundTripChecks/FeedRoundTripChecks.csproj'
)

foreach ($checkProject in $checkProjects)
{
    dotnet format whitespace $checkProject --no-restore --include tests
    dotnet format style $checkProject --no-restore --include tests --severity info --diagnostics IDE0005 IDE0011 IDE0063 IDE0065 IDE0161
}
```

Add `--verify-no-changes` to a formatting command to check without editing. A nonzero exit code means formatting changes are required or the command could not complete; read the diagnostics. Review the diff and build the affected projects after applying cleanup.

References: [Visual Studio Code Cleanup](https://learn.microsoft.com/en-us/visualstudio/ide/code-styles-and-code-cleanup), [.NET code style options](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/code-style-rule-options), [dotnet format](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-format), [StyleCop region rule](https://github.com/DotNetAnalyzers/StyleCopAnalyzers/blob/master/documentation/SA1124.md), [StyleCop ordering rule](https://github.com/DotNetAnalyzers/StyleCopAnalyzers/blob/master/documentation/SA1201.md).
