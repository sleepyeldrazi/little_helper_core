namespace LittleHelper;

/// <summary>
/// Code-aware compression for tool outputs during observation masking.
/// Preserves structure (signatures, imports, declarations) while stripping bodies.
/// Extracted from Compaction.cs to keep files under 300 lines (Rule #8).
/// </summary>
public static class CodeCompressor
{
    /// <summary>Maximum lines to preserve per compressed file.</summary>
    private const int MaxPreservedLines = 60;

    /// <summary>
    /// Compress code output by preserving signatures and stripping bodies.
    /// Keeps: imports/usings, class/interface/struct/enum declarations,
    /// method signatures, property declarations, field declarations.
    /// Strips: method bodies (content between { }), blank lines, comments.
    /// Returns null if nothing useful can be extracted.
    /// </summary>
    public static string? Compress(string content)
    {
        var lines = content.Split('\n');
        var preserved = new List<string>();
        bool inBody = false;
        int braceDepth = 0;
        int preservedLines = 0;

        for (int i = 0; i < lines.Length && preservedLines < MaxPreservedLines; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            // Skip empty lines in body
            if (inBody && string.IsNullOrWhiteSpace(trimmed))
                continue;

            // Track brace depth for body detection
            if (inBody)
            {
                braceDepth += CountChar(trimmed, '{') - CountChar(trimmed, '}');
                if (braceDepth <= 0)
                {
                    inBody = false;
                    preserved.Add(line);
                    preservedLines++;
                }
                continue;
            }

            // Always preserve: file header lines (e.g. "File: path (N lines)")
            if (trimmed.StartsWith("File:") || trimmed.StartsWith("Showing"))
            {
                preserved.Add(line);
                preservedLines++;
                continue;
            }

            // Always preserve: line number + signature/declaration lines
            if (IsSignatureLine(trimmed))
            {
                preserved.Add(line);
                preservedLines++;

                // If line opens a body (has {), enter body mode
                if (trimmed.Contains('{'))
                {
                    braceDepth = CountChar(trimmed, '{') - CountChar(trimmed, '}');
                    if (braceDepth > 0)
                        inBody = true;
                }
                continue;
            }

            // Always preserve: imports/usings/namespace/package
            if (IsImportLine(trimmed))
            {
                preserved.Add(line);
                preservedLines++;
                continue;
            }

            // Preserve field/property assignments at class level (no opening brace)
            if (trimmed.Length > 0 && !trimmed.StartsWith("//") &&
                !trimmed.StartsWith("*") && !trimmed.StartsWith("/*") &&
                !trimmed.Contains('{') && !trimmed.StartsWith("File:"))
            {
                preserved.Add(line);
                preservedLines++;
            }
        }

        if (preserved.Count == 0)
            return null;

        var result = string.Join('\n', preserved);
        result += "\n\n[Code compressed: showing signatures and declarations only. " +
                  "Use read tool to see full implementations.]";
        return result;
    }

    /// <summary>Check if a file path looks like source code or structured text.</summary>
    public static bool IsCodeFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".cs" or ".py" or ".js" or ".ts" or ".go" or ".rs" or ".java"
            or ".cpp" or ".c" or ".h" or ".hpp" or ".rb" or ".kt" or ".swift"
            or ".scala" or ".zig" or ".nim"
            or ".md" or ".mdx" or ".txt" or ".rst";  // Documentation/text files need structure preservation too
    }

    /// <summary>Check if a line is a code signature or declaration.</summary>
    private static bool IsSignatureLine(string trimmed)
    {
        if (string.IsNullOrWhiteSpace(trimmed)) return false;
        // Method/constructor declarations
        if (trimmed.StartsWith("public ") || trimmed.StartsWith("private ") ||
            trimmed.StartsWith("protected ") || trimmed.StartsWith("internal "))
            return true;
        // Static/override/async/virtual/abstract modifiers
        if (trimmed.StartsWith("static ") || trimmed.StartsWith("override ") ||
            trimmed.StartsWith("async ") || trimmed.StartsWith("virtual ") ||
            trimmed.StartsWith("abstract "))
            return true;
        // Type declarations
        if (trimmed.StartsWith("class ") || trimmed.StartsWith("interface ") ||
            trimmed.StartsWith("struct ") || trimmed.StartsWith("enum ") ||
            trimmed.StartsWith("record "))
            return true;
        // Function declarations (Python, JS, Go, Rust)
        if (trimmed.StartsWith("def ") || trimmed.StartsWith("func ") ||
            trimmed.StartsWith("fn ") || trimmed.StartsWith("function "))
            return true;
        // Attributes/annotations
        if (trimmed.StartsWith("[") && (trimmed.EndsWith("]") || trimmed.EndsWith(",")))
            return true;
        // Go/Rust type declarations
        if (trimmed.StartsWith("type ") || trimmed.StartsWith("impl "))
            return true;
        return false;
    }

    /// <summary>Check if a line is an import/using/namespace declaration.</summary>
    private static bool IsImportLine(string trimmed)
    {
        return trimmed.StartsWith("using ") || trimmed.StartsWith("import ") ||
               trimmed.StartsWith("from ") || trimmed.StartsWith("namespace ") ||
               trimmed.StartsWith("package ") || trimmed.StartsWith("#include") ||
               trimmed.StartsWith("require(") || trimmed.StartsWith("mod ");
    }

    private static int CountChar(string s, char c) => s.Count(ch => ch == c);
}
