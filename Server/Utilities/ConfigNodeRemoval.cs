using LunaConfigNode.CfgNode;
using System;
using System.Linq;

namespace Server.Utilities
{
    /// <summary>
    /// Use these instead of LunaConfigNode's <c>ConfigNode.RemoveNode(ConfigNode)</c>. That overload never takes the node
    /// out of the list the serializer writes (MixedCollection.Remove compares each stored ConfigNode with the
    /// CfgNodeValue wrapper, which never matches), so the "removed" node is still saved to disk, and it deep-compares
    /// every sibling with the same name. A "remove every X, then add the new X" rewrite therefore appended a full copy
    /// on every call (80,008 Science nodes for 368 subjects on the train server) and the first rewrite after a restart
    /// froze the server for minutes. <c>RemoveNode(string)</c> removes by name correctly and in one pass.
    /// </summary>
    public static class ConfigNodeRemoval
    {
        /// <summary>Removes every direct child named <paramref name="name"/>.</summary>
        public static void RemoveAllNodes(ConfigNode parent, string name)
        {
            parent?.RemoveNode(name);
        }

        /// <summary>
        /// Removes the direct children named <paramref name="name"/> for which <paramref name="shouldRemove"/> is true.
        /// The others are kept in their original order, after the siblings that have other names.
        /// </summary>
        public static void RemoveNodesWhere(ConfigNode parent, string name, Func<ConfigNode, bool> shouldRemove)
        {
            if (parent == null) return;

            var kept = parent.GetNodes(name)
                .Select(item => item.Value)
                .Where(node => node != null && !shouldRemove(node))
                .ToList();

            parent.RemoveNode(name);
            foreach (var node in kept)
            {
                parent.AddNode(node);
            }
        }
    }
}
