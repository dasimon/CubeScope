using CubeScope.Core.Project;

namespace CubeScope.Core.Tests;

public class FileBrowserServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cubescope-fb-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void List_ReturnsSubdirsAndCubeFiles_NotOtherFiles()
    {
        string sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);
        string aCube = Path.Combine(_dir, "a.cube");
        string bCube = Path.Combine(_dir, "b.cube");
        File.WriteAllText(aCube, "a");
        File.WriteAllText(bCube, "b");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "n");

        var svc = new FileBrowserService();
        var listing = svc.List(_dir);

        Assert.Equal(_dir, listing.Path);
        Assert.NotNull(listing.Parent);
        Assert.Contains(listing.Directories, e => e.Name == "sub" && e.IsDirectory);
        Assert.Equal(new[] { "a.cube", "b.cube" }, listing.CubeFiles.Select(f => f.Name));
        Assert.DoesNotContain(listing.CubeFiles, f => f.Name == "notes.txt");
        Assert.DoesNotContain(listing.Directories, d => d.Name == "notes.txt");
    }

    [Fact]
    public void List_FileArg_UsesItsDirectory()
    {
        string aCube = Path.Combine(_dir, "a.cube");
        File.WriteAllText(aCube, "a");

        var svc = new FileBrowserService();
        var listing = svc.List(aCube);

        Assert.Equal(_dir, listing.Path);
        Assert.Contains(listing.CubeFiles, f => f.Name == "a.cube");
    }

    [Fact]
    public void List_NullOrMissing_FallsBackToUserProfile()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var svc = new FileBrowserService();

        Assert.Equal(userProfile, svc.List(null).Path);
        Assert.Equal(userProfile, svc.List(@"Z:\does\not\exist_" + Guid.NewGuid()).Path);
    }
}
