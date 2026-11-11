using System.Linq;
using System.Text.Json.Nodes;
using MAAUnified.Application.Services.WebApi;
using Xunit;

namespace MAAUnified.Tests;

public sealed class WebApiTaskStoreTests
{
    [Fact]
    public void Append_PreservesCoreAssignedIds()
    {
        var store = new WebApiTaskStore();
        var first = store.Append(new WebApiTaskDefinition(41, "Fight", "first", new JsonObject { ["target"] = "enemy" }, true));
        var second = store.Append(new WebApiTaskDefinition(87, "Recruit", "second", new JsonObject(), false));

        Assert.Equal(41, first.Id);
        Assert.Equal(87, second.Id);
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void Append_RejectsDuplicateTaskTypeAndSyntheticId()
    {
        var store = new WebApiTaskStore();
        store.Append(new WebApiTaskDefinition(41, "Fight", "first", new JsonObject(), true));

        Assert.Throws<InvalidOperationException>(() =>
            store.Append(new WebApiTaskDefinition(42, "Fight", "second", new JsonObject(), true)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.Append(new WebApiTaskDefinition(0, "Recruit", "recruit", new JsonObject(), true)));
    }

    [Fact]
    public void Modify_UpdatesExistingEntry()
    {
        var store = new WebApiTaskStore();
        var entry = store.Append(new WebApiTaskDefinition(41, "Fight", "start", new JsonObject { ["stage"] = "1-1" }, true));
        var update = entry with { Name = "evolved", Enabled = false };

        var modified = store.TryModify(entry.Id, update);
        Assert.True(modified);

        var snapshot = store.List();
        var updated = snapshot.Single(task => task.Id == entry.Id);
        Assert.Equal("evolved", updated.Name);
        Assert.False(updated.Enabled);
    }
}
