using Ch13.Beacon.Agent;

namespace Ch13.Tests;

/// <summary>Reading tool arguments that are still arriving.</summary>
public sealed class PartialJsonTests
{
    [Theory]
    [InlineData("", null, null)]
    [InlineData("{", "{}", null)]
    [InlineData("{\"summ", "{}", null)]                                     // a key with no value yet
    [InlineData("{\"summary\"", "{}", null)]
    [InlineData("{\"summary\":", "{}", null)]
    [InlineData("{\"summary\":\"", "{\"summary\":\"\"}", "summary")]
    [InlineData("{\"summary\":\"The unit short-cyc", "{\"summary\":\"The unit short-cyc\"}", "summary")]
    [InlineData("{\"a\":\"done\",\"b\":\"half", "{\"a\":\"done\",\"b\":\"half\"}", "b")]
    [InlineData("{\"a\":\"done\",", "{\"a\":\"done\"}", null)]                // a dangling comma
    [InlineData("{\"a\":\"x\\", "{\"a\":\"x\"}", "a")]                       // half an escape
    [InlineData("{\"a\":\"x\\u00", "{\"a\":\"x\"}", "a")]                    // half a \u escape
    [InlineData("{\"a\":\"say \\\"hi", "{\"a\":\"say \\u0022hi\"}", "a")]      // an escaped quote doesn't end the string
    [InlineData("{\"n\":12", "{}", null)]                                    // 12 may become 125
    [InlineData("{\"n\":12,", "{\"n\":12}", null)]
    [InlineData("{\"ok\":tr", "{}", null)]
    [InlineData("{\"steps\":[\"Review\",\"Rea", "{\"steps\":[\"Review\",\"Rea\"]}", null)]
    [InlineData("{\"o\":{\"p\":\"q", "{\"o\":{\"p\":\"q\"}}", null)]        // only top-level properties are reported open
    [InlineData("{\"a\":\"b\"}", "{\"a\":\"b\"}", null)]                     // complete
    public void Completes_a_prefix_with_the_fewest_honest_characters(string prefix, string? expected, string? open)
    {
        var node = PartialJson.Parse(prefix, out var stillWriting);

        Assert.Equal(expected, node?.ToJsonString());
        Assert.Equal(open, stillWriting);
    }

    [Fact]
    public void Every_prefix_of_a_real_call_parses()
    {
        const string arguments = "{\"workOrderId\":\"WO-1001\",\"summary\":\"Short-cycling: \\\"E5\\\" trips.\",\"steps\":[1,22,333],\"ok\":true}";

        for (var length = 1; length <= arguments.Length; length++)
        {
            Assert.IsType<System.Text.Json.Nodes.JsonObject>(PartialJson.Parse(arguments[..length], out _));
        }

        Assert.Equal(System.Text.Json.Nodes.JsonNode.Parse(arguments)!.ToJsonString(), PartialJson.Parse(arguments, out _)!.ToJsonString());
    }
}
