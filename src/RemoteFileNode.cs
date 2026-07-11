using System.Collections.Generic;

namespace LingCodeFTP
{
    // A node in the remote file tree. Port of the Obj-C RemoteFileNode.
    public class RemoteFileNode
    {
        public string Name;
        public string FullPath;
        public bool IsDirectory;
        public long FileSize;
        public string DateString;
        public List<RemoteFileNode> Children;
        public bool ChildrenLoaded;
        public bool IsLoading;

        public RemoteFileNode(string name, string path, bool isDir, long size, string date)
        {
            Name = name ?? "";
            FullPath = path ?? "/";
            IsDirectory = isDir;
            FileSize = size;
            DateString = date ?? "";
            Children = isDir ? new List<RemoteFileNode>() : null;
            ChildrenLoaded = false;
            IsLoading = false;
        }
    }
}
