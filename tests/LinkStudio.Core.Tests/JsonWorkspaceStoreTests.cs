using LinkStudio.Core.Workspaces;
using Xunit;

namespace LinkStudio.Core.Tests
{
    public sealed class JsonWorkspaceStoreTests : IDisposable
    {
        private readonly string _tempDir;

        public JsonWorkspaceStoreTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "LinkStudio_WorkspaceTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [Fact]
        public async Task LoadAllAsync_ReturnsEmptyWhenDirectoryMissing()
        {
            var missingDir = Path.Combine(_tempDir, "not-exists");
            using var store = new JsonWorkspaceStore(missingDir);

            var all = await store.LoadAllAsync();

            Assert.Empty(all);
        }

        [Fact]
        public async Task SaveAndLoadAllAsync_RoundTripsWorkspaceAndDataSourceBinding()
        {
            using var store = new JsonWorkspaceStore(_tempDir);
            var doc = WorkspaceDocument.CreateImuTemplate("飞控 IMU 调参");
            doc.Widgets[0].DataSourceId = "tcp-imu-1";

            await store.SaveAsync(doc);
            var loaded = await store.LoadAllAsync();

            var restored = Assert.Single(loaded);
            Assert.Equal(doc.Id, restored.Id);
            Assert.Equal("飞控 IMU 调参", restored.Name);
            Assert.Equal(2, restored.Widgets.Count);
            Assert.Equal("tcp-imu-1", restored.Widgets[0].DataSourceId);
            Assert.Equal(["imu.ax", "imu.ay", "imu.az", "temp"], restored.Widgets[0].BoundFields);
            Assert.Equal(WidgetKind.Waveform, restored.Widgets[1].Type);
            Assert.Equal(["imu.ax", "imu.ay", "imu.az"], restored.Widgets[1].BoundFields);
        }

        [Fact]
        public async Task LoadAllAsync_SkipsCorruptWorkspaceFileWithoutBreakingOthers()
        {
            using var store = new JsonWorkspaceStore(_tempDir);
            var validDoc = WorkspaceDocument.CreateGnssTemplate("室外 GNSS");
            await store.SaveAsync(validDoc);
            await File.WriteAllTextAsync(Path.Combine(_tempDir, "broken.linkworkspace"), "{ not valid json");

            var loaded = await store.LoadAllAsync();

            var only = Assert.Single(loaded);
            Assert.Equal(validDoc.Id, only.Id);
            Assert.Equal("室外 GNSS", only.Name);
        }

        [Fact]
        public async Task DeleteAsync_RemovesSavedWorkspaceFile()
        {
            using var store = new JsonWorkspaceStore(_tempDir);
            var doc = WorkspaceDocument.CreateBlank("临时画板");
            await store.SaveAsync(doc);

            await store.DeleteAsync(doc.Id);
            var loaded = await store.LoadAllAsync();

            Assert.Empty(loaded);
        }

        [Fact]
        public void SerializeAndTryDeserialize_SupportsTemplateSharing()
        {
            using var store = new JsonWorkspaceStore(_tempDir);
            var doc = WorkspaceDocument.CreateImuTemplate("可分享模板");

            var json = store.Serialize(doc);
            var parsed = store.TryDeserialize(json);

            Assert.NotNull(parsed);
            Assert.Equal(doc.Name, parsed!.Name);
            Assert.Equal(doc.Widgets.Count, parsed.Widgets.Count);
            Assert.Null(store.TryDeserialize("invalid-json"));
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
    }
}
