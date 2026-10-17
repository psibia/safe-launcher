using System.Collections.Generic;
using KillSwitchLauncher.Services;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>Ключ лицензии: приведение к одному виду, хеш как в license-hashes.txt, разбор этого файла.</summary>
public class LicenseTests
{
    // Не настоящий ключ: только для проверки хеша.
    private const string Key = "ABCDE-FGHJK-LMNPQ-RSTUV-WXYZ2";
    private const string KeyHash = "a4e4e92425ff5afc006a53fb36c690b9cb21f066bf79aa66737543cb1335fbed";

    [Fact]
    public void Normalize_DropsDashesAndSpacesAndUppercases()
    {
        Assert.Equal("ABCDEFGHJKLMNPQRSTUVWXYZ2", License.Normalize(" abcde-fghjk lmnpq-RSTUV-wxyz2 "));
    }

    [Fact]
    public void Hash_MatchesLicenseHashesFormat()
    {
        Assert.Equal(KeyHash, License.Hash(Key));
        Assert.Equal(KeyHash, License.Hash(Key.ToLowerInvariant()));
    }

    [Fact]
    public void IsValid_ChecksKeyAgainstHashes()
    {
        var hashes = new HashSet<string> { KeyHash };
        Assert.True(License.IsValid(Key, hashes));
        Assert.True(License.IsValid("abcdefghjklmnpqrstuvwxyz2", hashes));
        Assert.False(License.IsValid("ABCDE-FGHJK-LMNPQ-RSTUV-WXYZ3", hashes));
        Assert.False(License.IsValid("", hashes));
        Assert.False(License.IsValid("---", new HashSet<string> { License.Hash("") }));
    }

    [Fact]
    public void ParseHashes_SkipsCommentsAndBadLines()
    {
        var hashes = License.ParseHashes("; комментарий\r\n" + KeyHash.ToUpperInvariant() + "\r\n\r\nне хеш\n" + new string('0', 63) + "\n");
        Assert.Equal(new HashSet<string> { KeyHash }, hashes);
    }
}
