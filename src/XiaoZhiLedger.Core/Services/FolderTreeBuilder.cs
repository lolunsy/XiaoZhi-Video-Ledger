using XiaoZhiLedger.Core.Models;

namespace XiaoZhiLedger.Core.Services;

public static class FolderTreeBuilder
{
    public static FolderNodeModel Build(
        string rootPath,
        IEnumerable<string> directories,
        IEnumerable<MediaItem> items,
        Func<MediaItem, string> statusSelector)
    {
        var rootName = new DirectoryInfo(rootPath).Name;
        if (string.IsNullOrWhiteSpace(rootName))
        {
            rootName = rootPath;
        }

        var root = new FolderNodeModel(rootName, rootPath);
        var nodes = new Dictionary<string, FolderNodeModel>(StringComparer.OrdinalIgnoreCase)
        {
            [rootPath] = root
        };

        foreach (var directory in directories
                     .Where(path => !string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path.Length)
                     .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var parentPath = Directory.GetParent(directory)?.FullName;
            if (parentPath is null || !nodes.TryGetValue(parentPath, out var parent))
            {
                continue;
            }

            var node = new FolderNodeModel(new DirectoryInfo(directory).Name, directory);
            parent.Children.Add(node);
            nodes[directory] = node;
        }

        foreach (var node in nodes.Values)
        {
            node.Children.Sort((left, right) =>
                StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
        }

        foreach (var item in items)
        {
            var currentPath = item.Folder;
            while (nodes.TryGetValue(currentPath, out var node))
            {
                node.TotalCount++;
                if (statusSelector(item) == "unused")
                {
                    node.UnusedCount++;
                }

                if (string.Equals(currentPath, rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                currentPath = Directory.GetParent(currentPath)?.FullName ?? "";
            }
        }

        return root;
    }
}
