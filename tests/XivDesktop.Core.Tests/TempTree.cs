namespace XivDesktop.Core.Tests;

/// <summary>A throwaway directory that acts as the Linux root: HostPaths maps "/x" to "&lt;temp&gt;/x".</summary>
internal sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "xivdesktop-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = new HostPaths("/home/user", p => Root + p);
    }

    public string Root { get; }

    public HostPaths Paths { get; }

    /// <summary>Writes a file at a Linux-style path below the fake root and returns its local path.</summary>
    public string Write(string linuxPath, string content = "")
    {
        var local = Root + linuxPath;
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.WriteAllText(local, content);
        return local;
    }

    public string Local(string linuxPath) => Root + linuxPath;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Samples
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", name));

    public static DesktopEntry Parse(string name) => DesktopEntryParser.Parse(Read(name), name)!;
}
