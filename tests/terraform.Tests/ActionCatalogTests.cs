using terraform.lib.engine;
using terraform.lib.utils;

namespace terraform.Tests;

public sealed class ActionCatalogTests
{
    [Fact]
    public void Every_action_has_a_unique_type_and_a_short_description()
    {
        var actions = ActionCatalog.Create(new FakeProcessRunner());

        Assert.Equal(6, actions.Count);
        Assert.Equal(actions.Count, actions.Select(action => action.ActionType).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(actions, action => Assert.False(string.IsNullOrWhiteSpace(action.ActionDescription)));
    }
}
