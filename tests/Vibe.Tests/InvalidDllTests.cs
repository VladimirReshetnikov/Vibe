using System;
using System.IO;
using Vibe.Decompiler;
using Xunit;

public class InvalidDllTests
{
    [Fact]
    public void SummaryIncludesBasicInfo()
    {
        var temp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(temp, new byte[] {1,2,3});
            var info = new InvalidDll(temp, new Exception("parse error"));
            var summary = info.GetSummary();
            Assert.Contains(temp, summary);
            Assert.Contains("3 bytes", summary);
            Assert.Contains("MD5:", summary);
            Assert.Contains("Error: parse error", summary);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
