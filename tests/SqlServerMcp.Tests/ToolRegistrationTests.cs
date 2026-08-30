using SqlServerMcp.Tools;

namespace SqlServerMcp.Tests;

public sealed class ToolRegistrationTests
{
    [Fact]
    public void AllRegisteredToolsRemainReadOnly()
    {
        var toolMethods = typeof(SqlServerMcpTools)
            .GetMethods()
            .Select(method => new
            {
                Method = method,
                Attribute = method.GetCustomAttributes(inherit: false)
                    .SingleOrDefault(attribute => attribute.GetType().Name == "McpServerToolAttribute")
            })
            .Where(item => item.Attribute is not null)
            .ToArray();

        Assert.Equal(44, toolMethods.Length);
        Assert.Contains(toolMethods, tool => tool.Method.Name == nameof(SqlServerMcpTools.ReplaceReportPayloadFragment));
        foreach (var tool in toolMethods)
        {
            var readOnlyProperty = tool.Attribute!.GetType().GetProperty("ReadOnly");
            Assert.NotNull(readOnlyProperty);
            Assert.Equal(true, readOnlyProperty.GetValue(tool.Attribute));
        }
    }
}
