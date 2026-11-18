using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Tests;

public class UserMappingsTests
{
    [Fact]
    public void Parse_MapsEachSignInAddressToItsAgent()
    {
        var mappings = UserMappings.Parse(
            "alice@example.com=team@example.com; bob@example.com=team@example.com; carol@example.com=carol@example.com");

        Assert.Equal(3, mappings.Count);
        Assert.True(mappings.TryGetZendeskEmail("alice@example.com", out var alice));
        Assert.Equal("team@example.com", alice);
        Assert.True(mappings.TryGetZendeskEmail("bob@example.com", out var bob));
        Assert.Equal("team@example.com", bob);
        Assert.True(mappings.TryGetZendeskEmail("carol@example.com", out var carol));
        Assert.Equal("carol@example.com", carol);
    }

    [Fact]
    public void Parse_EntryWithoutEquals_MapsAddressToItself()
    {
        var mappings = UserMappings.Parse("carol@example.com");

        Assert.True(mappings.TryGetZendeskEmail("carol@example.com", out var carol));
        Assert.Equal("carol@example.com", carol);
    }

    [Fact]
    public void Parse_AcceptsCommasNewLinesAndSpaces()
    {
        var mappings = UserMappings.Parse(" alice@example.com = team@example.com ,\r\n bob@example.com=team@example.com\n\n");

        Assert.Equal(2, mappings.Count);
        Assert.True(mappings.TryGetZendeskEmail("bob@example.com", out _));
    }

    [Fact]
    public void Parse_CombinesSeveralValues()
    {
        var mappings = UserMappings.Parse(["alice@example.com=team@example.com", null, "", "bob@example.com"]);

        Assert.Equal(2, mappings.Count);
    }

    [Fact]
    public void TryGetZendeskEmail_IgnoresCaseAndSurroundingSpace()
    {
        var mappings = UserMappings.Parse("Alice@Example.com=team@example.com");

        Assert.True(mappings.TryGetZendeskEmail("  alice@EXAMPLE.com ", out var agent));
        Assert.Equal("team@example.com", agent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("mallory@example.com")]
    public void TryGetZendeskEmail_ReturnsFalse_ForBlankOrUnmappedAddress(string? signInEmail)
    {
        var mappings = UserMappings.Parse("alice@example.com=team@example.com");

        Assert.False(mappings.TryGetZendeskEmail(signInEmail, out var agent));
        Assert.Null(agent);
    }

    [Theory]
    [InlineData("alice@example.com=team@example.com=other@example.com")]
    [InlineData("alice.example.com=team@example.com")]
    [InlineData("alice@example.com=")]
    [InlineData("=team@example.com")]
    [InlineData("alice@@example.com")]
    [InlineData("alice@")]
    [InlineData("alice smith@example.com")]
    public void Parse_Throws_ForMalformedEntry(string value)
    {
        Assert.Throws<FormatException>(() => UserMappings.Parse(value));
    }

    [Fact]
    public void Parse_Throws_WhenOneAddressIsMappedToTwoAgents()
    {
        var ex = Assert.Throws<FormatException>(() =>
            UserMappings.Parse("alice@example.com=team@example.com; ALICE@example.com=other@example.com"));

        Assert.Contains("mapped twice", ex.Message);
    }

    [Fact]
    public void Parse_AllowsTheSameEntryTwice()
    {
        var mappings = UserMappings.Parse("alice@example.com=team@example.com; alice@example.com=TEAM@example.com");

        Assert.Equal(1, mappings.Count);
    }

    [Fact]
    public void Parse_NothingSet_IsEmpty()
    {
        Assert.Equal(0, UserMappings.Parse((string?)null).Count);
    }
}

public class UserLoginModesTests
{
    [Theory]
    [InlineData(null, UserLoginMode.Shared)]
    [InlineData("", UserLoginMode.Shared)]
    [InlineData("  ", UserLoginMode.Shared)]
    [InlineData("Shared", UserLoginMode.Shared)]
    [InlineData("mapped", UserLoginMode.Mapped)]
    [InlineData(" MAPPED ", UserLoginMode.Mapped)]
    public void Parse_RecognisesModes(string? value, UserLoginMode expected)
    {
        Assert.Equal(expected, UserLoginModes.Parse(value));
    }

    [Theory]
    [InlineData("Mapping")]
    [InlineData("1")]
    [InlineData("Shared,Mapped")]
    public void Parse_Throws_ForUnknownMode(string value)
    {
        Assert.Throws<FormatException>(() => UserLoginModes.Parse(value));
    }
}
