namespace LinkStudio.Core.Workspaces
{
    /// <summary>
    /// 自定义工作空间（.linkworkspace）的持久化存储契约。
    /// </summary>
    public interface IWorkspaceStore : IDisposable
    {
        string DirectoryPath { get; }

        Task<IReadOnlyList<WorkspaceDocument>> LoadAllAsync(CancellationToken ct = default);

        Task SaveAsync(WorkspaceDocument document, CancellationToken ct = default);

        Task DeleteAsync(string workspaceId, CancellationToken ct = default);

        string Serialize(WorkspaceDocument document);

        WorkspaceDocument? TryDeserialize(string json);
    }
}
