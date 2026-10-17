using System;
using System.IO;
using System.Security.Cryptography;
using SafeLauncher.Updates;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>Подпись выпусков: что подписанный установщик проходит проверку, а изменённый или чужой — нет.</summary>
public sealed class UpdatePackageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SafeLauncherTests-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public UpdatePackageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string Setup(int size = 4096)
    {
        var path = Path.Combine(_dir, "SafeLauncher-Setup-2.4.0.0.exe");
        var bytes = new byte[size];
        new Random(1).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private ECDsa PublicOnly()
    {
        var key = ECDsa.Create();
        key.ImportFromPem(_key.ExportSubjectPublicKeyInfoPem());
        return key;
    }

    [Fact]
    public void SignedSetup_Verifies()
    {
        var setup = Setup();
        var text = UpdatePackage.Sign(setup, new Version(2, 4, 0, 0), _key);
        using var publicKey = PublicOnly();

        var manifest = UpdatePackage.Verify(setup, text, publicKey);

        Assert.Equal(new Version(2, 4, 0), manifest.Version);
        Assert.Equal("SafeLauncher-Setup-2.4.0.0.exe", manifest.FileName);
        Assert.Equal(4096, manifest.Size);
        // CRLF вместо LF (файл открыли и сохранили в Блокноте) подписи не портит.
        Assert.Equal(manifest, UpdatePackage.Verify(setup, text.Replace("\n", "\r\n"), publicKey));
    }

    [Fact]
    public void ChangedSetup_Fails()
    {
        var setup = Setup();
        var text = UpdatePackage.Sign(setup, new Version(2, 4, 0), _key);
        using var publicKey = PublicOnly();

        var bytes = File.ReadAllBytes(setup);
        bytes[100] ^= 1;
        File.WriteAllBytes(setup, bytes);
        Assert.Throws<UpdateSignatureException>(() => UpdatePackage.Verify(setup, text, publicKey));

        File.WriteAllBytes(setup, new byte[10]);
        Assert.Throws<UpdateSignatureException>(() => UpdatePackage.Verify(setup, text, publicKey));
    }

    [Fact]
    public void ChangedVersion_Fails()
    {
        var setup = Setup();
        var text = UpdatePackage.Sign(setup, new Version(2, 4, 0), _key);
        using var publicKey = PublicOnly();

        Assert.Throws<UpdateSignatureException>(() => UpdatePackage.Verify(setup, text.Replace("version: 2.4.0", "version: 9.0.0"), publicKey));
    }

    [Fact]
    public void OtherKey_Fails()
    {
        var setup = Setup();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var text = UpdatePackage.Sign(setup, new Version(2, 4, 0), other);
        using var publicKey = PublicOnly();

        Assert.Throws<UpdateSignatureException>(() => UpdatePackage.Verify(setup, text, publicKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("какой-то текст")]
    [InlineData("SafeLauncher update 1\nversion: 2.4.0\n")]
    [InlineData("SafeLauncher update 1\nversion: x\nfile: a.exe\nsize: 1\nsha256: 00\nsignature: AA==\n")]
    public void BrokenSignatureFile_Fails(string text)
    {
        var setup = Setup();
        using var publicKey = PublicOnly();
        Assert.Throws<UpdateSignatureException>(() => UpdatePackage.Verify(setup, text, publicKey));
    }

    [Theory]
    [InlineData("SafeLauncher-Setup-2.4.0.0.exe", "2.4.0")]
    [InlineData(@"C:\Output\SafeLauncher-Setup-2.10.3.exe", "2.10.3")]
    [InlineData("SafeLauncher-Setup.exe", null)]
    public void VersionFromFileName(string name, string? expected)
    {
        Assert.Equal(expected, UpdatePackage.VersionFromFileName(name)?.ToString(3));
    }
}
