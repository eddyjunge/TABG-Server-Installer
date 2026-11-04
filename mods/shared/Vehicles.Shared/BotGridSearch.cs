using System;
using System.Collections.Generic;
namespace TabgInstaller.Vehicles
{
    internal struct BotGridCell : IEquatable<BotGridCell>
    {
        internal int X,Z;
        internal BotGridCell(int x,int z){X=x;Z=z;}
        public bool Equals(BotGridCell other)=>X==other.X && Z==other.Z;
        public override bool Equals(object obj)=>obj is BotGridCell p && Equals(p);
        public override int GetHashCode()=>X*397^Z;
    }
    internal sealed class BotGridSearch
    {
        private struct Entry{internal BotGridCell Cell;internal int Cost,Rank;}
        private readonly BotGridCell _goal;
        private readonly int _radius,_limit;
        private readonly Func<BotGridCell,BotGridCell,bool> _edge;
        private readonly List<Entry> _heap=new List<Entry>();
        private readonly HashSet<BotGridCell> _closed=new HashSet<BotGridCell>();
        private readonly Dictionary<BotGridCell,int> _cost=new Dictionary<BotGridCell,int>();
        private readonly Dictionary<BotGridCell,BotGridCell> _previous=new Dictionary<BotGridCell,BotGridCell>();
        private BotGridCell _nearest;
        internal readonly List<BotGridCell> Path=new List<BotGridCell>();
        internal bool Done,Reached;
        internal int Expanded;
        internal BotGridSearch(int x,int z,int radius,Func<BotGridCell,BotGridCell,bool> edge,int limit=625)
        {_goal=new BotGridCell(x,z);_radius=radius;_edge=edge;_limit=limit;_nearest=new BotGridCell(0,0);_cost[_nearest]=0;Push(_nearest,0);}
        private int Distance(BotGridCell p)=>Math.Abs(_goal.X-p.X)+Math.Abs(_goal.Z-p.Z);
        private void Push(BotGridCell p,int cost)
        {
            var entry=new Entry{Cell=p,Cost=cost,Rank=(cost+Distance(p))*1000+Distance(p)};
            int i=_heap.Count;_heap.Add(entry);
            while(i>0){int parent=(i-1)/2;if(_heap[parent].Rank<=entry.Rank)break;_heap[i]=_heap[parent];i=parent;}_heap[i]=entry;
        }
        private Entry Pop()
        {
            var result=_heap[0];var last=_heap[_heap.Count-1];_heap.RemoveAt(_heap.Count-1);
            if(_heap.Count==0)return result;
            int i=0;
            while(i*2+1<_heap.Count){int child=i*2+1;if(child+1<_heap.Count && _heap[child+1].Rank<_heap[child].Rank)child++;
                if(last.Rank<=_heap[child].Rank)break;_heap[i]=_heap[child];i=child;}_heap[i]=last;return result;
        }
        private void Finish(BotGridCell current,bool reached)
        {
            Done=true;Reached=reached;
            // A blocked goal may still yield useful progress along a verified route.
            if(!reached && Distance(current)>Distance(new BotGridCell(0,0))-4)return;
            Path.Add(current);while(_previous.TryGetValue(current,out var parent)){current=parent;Path.Add(current);}Path.Reverse();
        }
        internal void Step(int budget)
        {
            while(!Done && budget>0)
            {
                if(_heap.Count==0 || Expanded>=_limit){Finish(_nearest,false);break;}
                var entry=Pop();var current=entry.Cell;
                if(_closed.Contains(current) || _cost[current]!=entry.Cost)continue;
                _closed.Add(current);Expanded++;budget--;
                if(Distance(current)<Distance(_nearest))_nearest=current;
                if(current.Equals(_goal)){Finish(current,true);break;}
                for(int i=0;i<4;i++)
                {
                    var next=new BotGridCell(current.X+(i==0?1:i==1?-1:0),current.Z+(i==2?1:i==3?-1:0));
                    if(Math.Abs(next.X)>_radius || Math.Abs(next.Z)>_radius || _closed.Contains(next) || !_edge(current,next))continue;
                    int score=_cost[current]+1;if(_cost.TryGetValue(next,out int old) && old<=score)continue;
                    _previous[next]=current;_cost[next]=score;Push(next,score);
                }
            }
        }
    }
}
