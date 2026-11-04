using System.Collections.Generic;
namespace TabgInstaller.Vehicles
{
    internal sealed class BotAllianceRules
    {
        private readonly Dictionary<byte,HashSet<byte>> _groups=new Dictionary<byte,HashSet<byte>>();
        private readonly Dictionary<HashSet<byte>,byte> _ids=new Dictionary<HashSet<byte>,byte>();
        internal byte TeamId(byte bot)=>_groups.TryGetValue(bot,out var group) && group.Count>1 && _ids.TryGetValue(group,out var id)?id:(byte)0;
        internal bool Friends(byte a,byte b)=>a==b || (_groups.TryGetValue(a,out var group) && group.Contains(b));
        internal int Size(byte a)=>_groups.TryGetValue(a,out var group)?group.Count:1;
        internal bool Join(byte a,byte b)
        {
            if(a==b || Friends(a,b))return false;
            var members=new HashSet<byte>();
            if(_groups.TryGetValue(a,out var first))members.UnionWith(first);else members.Add(a);
            if(_groups.TryGetValue(b,out var second))members.UnionWith(second);else members.Add(b);
            if(members.Count>3)return false;
            byte id=TeamId(a);if(id==0)id=TeamId(b);
            if(first!=null)_ids.Remove(first);if(second!=null)_ids.Remove(second);
            if(id==0){id=1;while(_ids.ContainsValue(id) && id<254)id++;}
            _ids[members]=id;
            foreach(byte member in members)_groups[member]=members;
            return true;
        }
        internal void Remove(byte a)
        {
            if(!_groups.TryGetValue(a,out var group))return;
            group.Remove(a);_groups.Remove(a);
            if(group.Count<=1){_ids.Remove(group);foreach(byte left in group)_groups.Remove(left);}
        }
        internal void Clear(){_groups.Clear();_ids.Clear();}
    }
}
