using System;
using System.IO;
using System.Collections.Generic;
namespace TabgInstaller.Vehicles
{
    // Optional trailer on native damage packets. Damage still uses the original game commands.
    internal static class BlastReceipt
    {
        private const int Magic=0x42544754;
        internal static byte[] Tag(byte[] payload,bool missile,int id)
        {
            if(payload==null || id<=0)return payload;
            using(var s=new MemoryStream())using(var w=new BinaryWriter(s))
            {w.Write(payload);w.Write(Magic);w.Write(missile);w.Write(id);return s.ToArray();}
        }
        internal static bool Read(byte[] payload,out bool missile,out int id,out byte[] native)
        {
            missile=false;id=0;native=payload;
            if(payload==null || payload.Length<19 || payload.Length>128)return false;
            int offset=payload.Length-9;
            if(BitConverter.ToInt32(payload,offset)!=Magic || payload[offset+4]>1)return false;
            missile=payload[offset+4]==1;id=BitConverter.ToInt32(payload,offset+5);
            if(id<=0)return false;
            native=new byte[offset];Array.Copy(payload,native,offset);return true;
        }
        internal static float HealthLost(float before,float after)
            =>float.IsNaN(before) || float.IsNaN(after) || float.IsInfinity(before) || float.IsInfinity(after)?0:Math.Max(0,before-Math.Max(0,after));
    }
    internal sealed class BlastConfirmationWindow
    {
        private readonly byte _owner;
        private readonly float _expires;
        private readonly HashSet<long> _confirmed=new HashSet<long>();
        internal BlastConfirmationWindow(byte owner,float now){_owner=owner;_expires=now+5;}
        internal bool Accept(byte attacker,bool vehicle,int target,float damage,float now)
        {
            if(attacker!=_owner || !(now<=_expires) || !(damage>0) || float.IsInfinity(damage) || target<0 || (!vehicle && target==attacker))return false;
            return _confirmed.Add(((long)target<<1)|(vehicle?1L:0L));
        }
    }
}
