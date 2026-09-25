using CubeScope.Core.Project;

namespace CubeScope.Core.Tests;

/// <summary>
/// Every path the API receives ends up in File/Directory/XDocument calls. A UNC path
/// (or an http:// URL, which XDocument.Load happily fetches) makes the server open an
/// outbound SMB/HTTP connection with the user's Windows credentials — the NTLM hash leaks
/// to whoever controls the target. The refusal must happen on the string alone, before any
/// disk access (File.Exists on a UNC path already connects).
/// </summary>
public class LocalPathGuardTests
{
    [Theory]
    [InlineData(@"\\attacker\share")]
    [InlineData(@"\\attacker\share\x.cube")]
    [InlineData(@"//attacker/share/x.cube")]
    [InlineData(@"\/attacker/share/x.cube")]
    [InlineData(@"\\?\UNC\attacker\share\x.cube")]
    [InlineData(@"\\?\C:\x.cube")]
    [InlineData(@"\\.\C:\x.cube")]
    [InlineData("http://attacker/x.cube")]
    [InlineData("file://attacker/share/x.cube")]
    [InlineData("file:///C:/x.cube")]
    [InlineData(@"relative\x.cube")]
    [InlineData(@"C:relative.cube")]
    [InlineData(@"\rooted-but-no-drive.cube")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Non_local_or_not_fully_qualified_paths_are_refused(string? path)
        => Assert.Throws<InvalidOperationException>(() => LocalPathGuard.EnsureLocal(path));

    [Theory]
    [InlineData(@"C:\Projects\Demo")]
    [InlineData(@"C:\Projects\Demo\CubeDemo.cube")]
    [InlineData(@"Z:\x")]
    [InlineData(@"C:/Projects/Demo")]
    public void Local_fully_qualified_paths_are_accepted(string path)
        => Assert.Equal(Path.GetFullPath(path), LocalPathGuard.EnsureLocal(path));

    [Fact]
    public void Cube_file_requires_the_cube_extension()
    {
        Assert.Equal(@"C:\p\CubeDemo.cube", LocalPathGuard.EnsureLocalCubeFile(@"C:\p\CubeDemo.cube"));
        Assert.Equal(@"C:\p\CubeDemo.CUBE", LocalPathGuard.EnsureLocalCubeFile(@"C:\p\CubeDemo.CUBE"));
        Assert.Throws<InvalidOperationException>(() => LocalPathGuard.EnsureLocalCubeFile(@"C:\Windows\win.ini"));
        Assert.Throws<InvalidOperationException>(() => LocalPathGuard.EnsureLocalCubeFile(@"C:\p\x.cube.bak"));
        Assert.Throws<InvalidOperationException>(() => LocalPathGuard.EnsureLocalCubeFile(@"\\attacker\s\x.cube"));
    }

    [Fact]
    public void FileBrowser_refuses_a_unc_path_instead_of_listing_it()
        => Assert.Throws<InvalidOperationException>(() => new FileBrowserService().List(@"\\attacker\share"));

    [Fact]
    public void Project_service_refuses_unc_and_urls_on_every_entry_point()
    {
        var svc = new CubeProjectService();
        Assert.Throws<InvalidOperationException>(() => svc.Load(@"\\attacker\share\x.cube"));
        Assert.Throws<InvalidOperationException>(() => svc.Load("http://attacker/x.cube"));
        Assert.Throws<InvalidOperationException>(() => svc.Save(@"\\attacker\share\x.cube", "CALCULATE;"));
        Assert.Throws<InvalidOperationException>(() => svc.GetCalculationProperties("http://attacker/x.cube"));
        Assert.Throws<InvalidOperationException>(() =>
            svc.SaveCalculationProperty(@"\\attacker\share\x.cube", "[Measures].[X]", null, null, null));
    }
}
