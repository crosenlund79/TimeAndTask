using WorkTrace.Core.Management;
using WorkTrace.Core.Validation;

namespace WorkTrace.Tests.Management;

public class ManagementValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ClientName_IsRequired(string? name)
    {
        var errors = ManagementValidator.Validate(new ClientInput(name, null, false));
        Assert.NotNull(errors.For(nameof(ClientInput.Name)));
    }

    [Fact]
    public void ClientName_LengthIsLimited()
    {
        var errors = ManagementValidator.Validate(new ClientInput(new string('x', 201), null, false));
        Assert.NotNull(errors.For(nameof(ClientInput.Name)));
    }

    [Fact]
    public void Task_RequiresClientAndTitle()
    {
        var errors = ManagementValidator.Validate(new TaskInput(null, null, "0001", " ", null, null));
        Assert.NotNull(errors.For(nameof(TaskInput.ClientId)));
        Assert.NotNull(errors.For(nameof(TaskInput.Title)));
        Assert.Null(errors.For(nameof(TaskInput.ExternalId)));
    }

    [Fact]
    public void Clean_TrimsButPreservesInnerText()
    {
        Assert.Equal("00042-b", Text.Clean("  00042-b "));
        Assert.Null(Text.Clean("   "));
    }

    [Theory]
    [InlineData("Ærø", "ærø", true)]
    [InlineData("Café", "cafe", true)]
    [InlineData("Contoso", "Contoso A/S", false)]
    public void SameName_IgnoresCaseAndAccents(string a, string b, bool expected)
    {
        Assert.Equal(expected, Text.SameName(a, b));
    }
}
