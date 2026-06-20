using ChoboServer.Services;

namespace Chobo.Tests;

public sealed class SqlLogRedactorTests
{
    [Fact]
    public void Preview_redacts_s3_credentials_but_keeps_query_shape()
    {
        var accessKey = "access-key-for-test";
        var secretKey = "secret-key-for-test";
        var sql = $"BACKUP TABLE `db`.`orders` TO {ClickHouseSql.S3("http://minio:9000/bucket/path", accessKey, secretKey)} ASYNC";

        var preview = SqlLogRedactor.Preview(sql, [accessKey, secretKey]);

        Assert.Contains("BACKUP TABLE `db`.`orders` TO S3", preview);
        Assert.Contains("http://minio:9000/bucket/path", preview);
        Assert.DoesNotContain(accessKey, preview);
        Assert.DoesNotContain(secretKey, preview);
        Assert.Equal(2, CountOccurrences(preview, "***REDACTED***"));
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }
}
