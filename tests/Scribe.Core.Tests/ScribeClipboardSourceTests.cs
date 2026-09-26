namespace Scribe.Core.Tests;

public sealed class ScribeClipboardSourceTests
{
    [Fact]
    public void App_clipboard_writes_go_through_ScribeClipboard()
    {
        var root = RepositoryRoot();
        var appRoot = Path.Combine(root, "src", "Scribe.App");
        var allowed = Path.Combine("src", "Scribe.App", "Infrastructure", "ScribeClipboard.cs");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file);
            if (relative.Equals(allowed, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var source = RemoveComments(File.ReadAllText(file));
            if (HasDirectClipboardWrite(source, "Clipboard.SetText(") ||
                HasDirectClipboardWrite(source, "Clipboard.SetDataObject("))
            {
                offenders.Add(relative);
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    private static bool IsBuildOutput(string file)
    {
        var separator = Path.DirectorySeparatorChar;
        return file.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase) ||
            file.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveComments(string source)
    {
        var result = new System.Text.StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                i += 2;
                while (i < source.Length && source[i] != '\r' && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && (source[i] != '*' || source[i + 1] != '/'))
                {
                    i++;
                }

                i++;
                continue;
            }

            result.Append(source[i]);
        }

        return result.ToString();
    }

    private static bool HasDirectClipboardWrite(string source, string call)
    {
        var index = source.IndexOf(call, StringComparison.Ordinal);
        while (index >= 0)
        {
            if (index == 0 || source[index - 1] != 'e')
            {
                return true;
            }

            index = source.IndexOf(call, index + call.Length, StringComparison.Ordinal);
        }

        return false;
    }
}
