using ServiceLib.Tests.CoreConfig;

namespace ServiceLib.Tests.Handler;

[NotInParallel]
public class BuiltinRoutingInitializationTests
{
    [Test]
    public async Task ConcurrentInitialization_CreatesOneDefaultSet_AndPreservesExplicitImport()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        await SQLiteHelper.Instance.DeleteAllAsync<RoutingItem>();
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ConfigHandler.InitBuiltinRouting(config)));
            var first = await AppManager.Instance.RoutingItems();
            await first.Should().HaveCount(3);
            await first.Select(item => item.Remarks).Distinct().Count().Should().BeEqualTo(3);
            await first.Count(item => item.IsActive).Should().BeEqualTo(1);

            await ConfigHandler.InitBuiltinRouting(config);
            var repeated = await AppManager.Instance.RoutingItems();
            await repeated.Select(item => item.Id).Order().ToArray().Should().BeEquivalentTo(first.Select(item => item.Id).Order().ToArray());

            await ConfigHandler.InitBuiltinRouting(config, true);
            var imported = await AppManager.Instance.RoutingItems();
            await imported.Should().HaveCount(6);
            await imported.Count(item => item.IsActive).Should().BeEqualTo(1);
        }
        finally { await SQLiteHelper.Instance.DeleteAllAsync<RoutingItem>(); }
    }
}
