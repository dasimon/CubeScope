using CubeScope.Core.Ai;
using CubeScope.Core.State;

namespace CubeScope.Core.Tests;

/// <summary>
/// The Anthropic key is read from an environment variable whose NAME is a setting, so that a
/// user can keep the key out of ANTHROPIC_API_KEY (which other tools, Claude Code among them,
/// pick up). Only the name is stored, never the key.
/// </summary>
public class AiKeyVariableTests
{
    private static string NewPath() => Path.Combine(Path.GetTempPath(), $"cs-aikey-{Guid.NewGuid():N}.db");

    [Fact]
    public void The_default_is_the_SDK_variable()
    {
        using var store = new StateStore(NewPath());
        Assert.Equal("ANTHROPIC_API_KEY", store.GetAiKeyVariable());
    }

    [Fact]
    public void A_chosen_name_is_trimmed_and_remembered()
    {
        string path = NewPath();
        using (var store = new StateStore(path))
            Assert.Equal("CLAUDE_ESSAIS_API_KEY", store.SetAiKeyVariable("  CLAUDE_ESSAIS_API_KEY "));
        using var reopened = new StateStore(path);
        Assert.Equal("CLAUDE_ESSAIS_API_KEY", reopened.GetAiKeyVariable());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_goes_back_to_the_default(string? name)
    {
        using var store = new StateStore(NewPath());
        store.SetAiKeyVariable("MY_KEY");
        Assert.Equal("ANTHROPIC_API_KEY", store.SetAiKeyVariable(name));
        Assert.Equal("ANTHROPIC_API_KEY", store.GetAiKeyVariable());
    }

    [Theory]
    [InlineData("1KEY")]
    [InlineData("MY-KEY")]
    [InlineData("MY KEY")]
    [InlineData("%ANTHROPIC_API_KEY%")]
    [InlineData("sk-ant-api03-abcdef")]
    public void An_invalid_name_is_refused_and_the_previous_one_kept(string name)
    {
        using var store = new StateStore(NewPath());
        store.SetAiKeyVariable("MY_KEY");
        var ex = Assert.Throws<ArgumentException>(() => store.SetAiKeyVariable(name));
        Assert.Null(ex.ParamName);
        Assert.Equal("MY_KEY", store.GetAiKeyVariable());
    }

    [Fact]
    public void A_name_longer_than_128_characters_is_refused()
    {
        Assert.True(AiService.IsValidKeyVariable(new string('A', 128)));
        Assert.False(AiService.IsValidKeyVariable(new string('A', 129)));
    }

    [Theory]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("CLAUDE_ESSAIS_API_KEY")]
    [InlineData("_key")]
    [InlineData("k2")]
    public void Valid_names(string name) => Assert.True(AiService.IsValidKeyVariable(name));

    [Fact]
    public void The_service_refuses_an_invalid_name_and_keeps_its_own()
    {
        var before = AiService.KeyVariable;
        Assert.Throws<ArgumentException>(() => AiService.KeyVariable = "NOT VALID");
        Assert.Equal(before, AiService.KeyVariable);
    }

    [Fact]
    public void The_not_configured_message_names_the_variable_in_use()
    {
        Assert.Contains(AiService.KeyVariable, AiService.NotConfiguredMessage, StringComparison.Ordinal);
    }
}
