using Avalonia.Controls;
using Omnix.Base;
using Reactive.Bindings;
using System.Collections.Generic;

namespace Omnix.Avalonia.Models.Primitives
{
    public abstract class TreeViewModelBase : DisposableBase, IDropable
    {
        public ReactiveProperty<string> Name { get; protected set; }
        public ReactiveProperty<bool> IsSelected { get; protected set; }
        public ReactiveProperty<bool> IsExpanded { get; protected set; }

        public TreeViewModelBase(TreeViewModelBase? parent)
        {
            this.Parent = parent;
        }

        public TreeViewModelBase? Parent { get; private set; }

        public IEnumerable<TreeViewModelBase> GetAncestors()
        {
            var list = new LinkedList<TreeViewModelBase>();
            list.AddFirst(this);

            for (; ; )
            {
                var parent = list.First.Value.Parent;
                if (parent == null)
                {
                    break;
                }

                list.AddFirst(parent);
            }

            return list;
        }

        public abstract bool TryAdd(object value);
        public abstract bool TryRemove(object value);
    }
}